package com.example.gamevault.data.repository

import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.DocumentsContract
import com.example.gamevault.data.local.AppDatabase
import com.example.gamevault.data.local.entity.ConsoleFamilyEntity
import com.example.gamevault.data.local.entity.ConsoleModelEntity
import com.example.gamevault.data.local.entity.GameEntity
import com.example.gamevault.data.local.entity.PcServiceEntity
import com.example.gamevault.data.local.model.DbGameNote
import com.example.gamevault.data.local.model.DbGameStatus
import com.example.gamevault.data.local.model.DbImageSourceType
import com.example.gamevault.data.local.model.DbPlatformType
import com.example.gamevault.data.local.model.DbStoredImage
import com.example.gamevault.data.local.mapper.toDomain
import com.example.gamevault.data.serialization.AppThemeJson
import com.example.gamevault.data.serialization.GameVaultLibraryFormat
import com.example.gamevault.domain.model.ImportInput
import com.example.gamevault.domain.repository.AppSettingsRepository
import com.example.gamevault.domain.repository.ExportedBackup
import com.example.gamevault.domain.repository.GameImportExportRepository
import com.example.gamevault.domain.repository.ImportConflict
import com.example.gamevault.domain.repository.ImportConflictDecision
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.UUID
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

class DefaultGameImportExportRepository(
    private val context: Context,
    private val database: AppDatabase,
    private val settingsRepository: AppSettingsRepository,
) : GameImportExportRepository {
    private val gameDao = database.gameDao()
    private val pcServiceDao = database.pcServiceDao()
    private val consoleFamilyDao = database.consoleFamilyDao()
    private val consoleModelDao = database.consoleModelDao()

    override suspend fun exportJson(): Result<String> = runCatching {
        withContext(Dispatchers.IO) {
            val fileName = "gamevault-${timestamp()}.json"
            val libraryJson = createLibraryJson(includeImageArchiveNames = false).toString(2).toByteArray()
            writeBackupFile(fileName, GameVaultLibraryFormat.JsonMimeType) { output ->
                output.write(libraryJson)
            }
            "JSON экспортирован: $fileName"
        }
    }

    override suspend fun exportZipWithImages(): Result<String> = runCatching {
        withContext(Dispatchers.IO) {
            val backup = createZipBackupPayload()
            writeBackupFile(backup.fileName, backup.mimeType) { output ->
                output.write(backup.bytes)
            }
            "ZIP резервная копия создана: ${backup.fileName}"
        }
    }

    override suspend fun createZipBackup(): Result<ExportedBackup> = runCatching {
        withContext(Dispatchers.IO) {
            createZipBackupPayload()
        }
    }

    override suspend fun importLibrary(
        input: ImportInput,
        resolveConflict: suspend (ImportConflict) -> ImportConflictDecision,
    ): Result<String> = runCatching {
        withContext(Dispatchers.IO) {
            val parsed = when (input) {
                is ImportInput.ContentUri -> parseImport(Uri.parse(input.uri.value))
                is ImportInput.Bytes -> parseImport(
                    displayName = input.fileName,
                    mimeType = input.mimeType,
                    inputProvider = { ByteArrayInputStream(input.bytes) },
                )
            }
            importParsed(parsed, resolveConflict)
        }
    }

    private suspend fun createZipBackupPayload(): ExportedBackup {
        val fileName = "gamevault-${timestamp()}.zip"
        val libraryJson = createLibraryJson(includeImageArchiveNames = true).toString(2).toByteArray()
        val output = ByteArrayOutputStream()
        ZipOutputStream(output).use { zip ->
            zip.putNextEntry(ZipEntry(GameVaultLibraryFormat.Archive.LibraryJson))
            zip.write(libraryJson)
            zip.closeEntry()

            exportImages(zip)
            exportDatabaseCopy(zip)
        }
        return ExportedBackup(
            fileName = fileName,
            mimeType = GameVaultLibraryFormat.ZipMimeType,
            bytes = output.toByteArray(),
        )
    }

    private suspend fun importParsed(
        parsed: ParsedImport,
        resolveConflict: suspend (ImportConflict) -> ImportConflictDecision,
    ): String {
        val pcServiceIds = importPcServices(parsed.json.optJSONArray(GameVaultLibraryFormat.Field.PcServices) ?: JSONArray())
        val familyIds = importConsoleFamilies(
            parsed.json.optJSONArray(GameVaultLibraryFormat.Field.ConsoleFamilies) ?: JSONArray(),
        )
        val modelIds = importConsoleModels(
            parsed.json.optJSONArray(GameVaultLibraryFormat.Field.ConsoleModels) ?: JSONArray(),
            familyIds,
        )
        val imported = importGames(
            games = parsed.json.optJSONArray(GameVaultLibraryFormat.Field.Games) ?: JSONArray(),
            pcServiceIds = pcServiceIds,
            familyIds = familyIds,
            modelIds = modelIds,
            imageFiles = parsed.imageFiles,
            resolveConflict = resolveConflict,
        )
        if (imported.canceled) {
            parsed.tempDir?.deleteRecursively()
            return "\u0418\u043c\u043f\u043e\u0440\u0442 \u043e\u0442\u043c\u0435\u043d\u0435\u043d."
        }
        settingsRepository.importCustomThemes(
            AppThemeJson.fromJson(parsed.json.optJSONObject(GameVaultLibraryFormat.Field.Themes)),
        )
        parsed.tempDir?.deleteRecursively()
        return "Импорт завершен. Добавлено: ${imported.added}, заменено: ${imported.replaced}, пропущено: ${imported.skipped}."
    }

    private suspend fun createLibraryJson(includeImageArchiveNames: Boolean): JSONObject {
        val imageNames = if (includeImageArchiveNames) {
            gameDao.getAll().associate { game ->
                game.id to game.imageLocalPath?.let { File(it).name }
            }
        } else {
            emptyMap()
        }

        return JSONObject()
            .put(GameVaultLibraryFormat.Field.Format, GameVaultLibraryFormat.FormatName)
            .put(GameVaultLibraryFormat.Field.Version, GameVaultLibraryFormat.CurrentVersion)
            .put(GameVaultLibraryFormat.Field.CreatedAt, System.currentTimeMillis())
            .put(GameVaultLibraryFormat.Field.PcServices, JSONArray(pcServiceDao.getAll().map(::pcServiceToJson)))
            .put(GameVaultLibraryFormat.Field.ConsoleFamilies, JSONArray(consoleFamilyDao.getAll().map(::consoleFamilyToJson)))
            .put(GameVaultLibraryFormat.Field.ConsoleModels, JSONArray(consoleModelDao.getAll().map(::consoleModelToJson)))
            .put(GameVaultLibraryFormat.Field.Themes, AppThemeJson.toJson(settingsRepository.settings.value.customThemes))
            .put(
                GameVaultLibraryFormat.Field.Games,
                JSONArray(
                    gameDao.getAll().map { game ->
                        gameToJson(
                            game = game,
                            imageArchiveName = imageNames[game.id],
                        )
                    },
                ),
            )
    }

    private fun pcServiceToJson(entity: PcServiceEntity): JSONObject =
        JSONObject()
            .put(GameVaultLibraryFormat.Field.Id, entity.id)
            .put(GameVaultLibraryFormat.Field.Name, entity.name)
            .put(GameVaultLibraryFormat.Field.IsDefault, entity.isDefault)

    private fun consoleFamilyToJson(entity: ConsoleFamilyEntity): JSONObject =
        JSONObject()
            .put(GameVaultLibraryFormat.Field.Id, entity.id)
            .put(GameVaultLibraryFormat.Field.Name, entity.name)
            .put(GameVaultLibraryFormat.Field.IsDefault, entity.isDefault)

    private fun consoleModelToJson(entity: ConsoleModelEntity): JSONObject =
        JSONObject()
            .put(GameVaultLibraryFormat.Field.Id, entity.id)
            .put(GameVaultLibraryFormat.Field.FamilyId, entity.familyId)
            .put(GameVaultLibraryFormat.Field.Name, entity.name)
            .put(GameVaultLibraryFormat.Field.IsDefault, entity.isDefault)

    private fun gameToJson(game: GameEntity, imageArchiveName: String?): JSONObject =
        JSONObject()
            .put(GameVaultLibraryFormat.Field.Id, game.id)
            .put(GameVaultLibraryFormat.Field.Title, game.title)
            .put(GameVaultLibraryFormat.Field.Year, game.year)
            .put(GameVaultLibraryFormat.Field.Status, game.statuses.firstOrNull()?.name ?: DbGameStatus.PLANNED.name)
            .put(GameVaultLibraryFormat.Field.Statuses, JSONArray(game.statuses.distinct().map { it.name }))
            .put(GameVaultLibraryFormat.Field.PlatformType, game.platformType.name)
            .put(GameVaultLibraryFormat.Field.PcServiceId, game.pcServiceId)
            .put(GameVaultLibraryFormat.Field.ConsoleFamilyId, game.consoleFamilyId)
            .put(GameVaultLibraryFormat.Field.ConsoleModelId, game.consoleModelId)
            .put(GameVaultLibraryFormat.Field.ImageLocalPath, game.imageLocalPath)
            .put(GameVaultLibraryFormat.Field.ImageArchiveName, imageArchiveName)
            .put(GameVaultLibraryFormat.Field.ImageSourceUrl, game.imageSourceUrl)
            .put(GameVaultLibraryFormat.Field.ImageSourceType, game.imageSourceType.name)
            .put(GameVaultLibraryFormat.Field.ImageScale, game.imageScale)
            .put(GameVaultLibraryFormat.Field.ImageOffsetX, game.imageOffsetX)
            .put(GameVaultLibraryFormat.Field.ImageOffsetY, game.imageOffsetY)
            .put(GameVaultLibraryFormat.Field.ImageGallery, JSONArray(game.imageGallery.map(::storedImageToJson)))
            .put(GameVaultLibraryFormat.Field.SourcePageUrl, game.sourcePageUrl)
            .put(GameVaultLibraryFormat.Field.CustomNotes, JSONArray(game.customNotes.map(::gameNoteToJson)))
            .put(GameVaultLibraryFormat.Field.CreatedAt, game.createdAt)
            .put(GameVaultLibraryFormat.Field.UpdatedAt, game.updatedAt)

    private fun gameNoteToJson(note: DbGameNote): JSONObject =
        JSONObject()
            .put(GameVaultLibraryFormat.Field.Category, note.category)
            .put(GameVaultLibraryFormat.Field.Text, note.text)

    private fun storedImageToJson(image: DbStoredImage): JSONObject =
        JSONObject()
            .put(GameVaultLibraryFormat.Field.LocalPath, image.localPath)
            .put(GameVaultLibraryFormat.Field.SourceUrl, image.sourceUrl)
            .put(GameVaultLibraryFormat.Field.SourceType, image.sourceType.name)

    private fun exportImages(zip: ZipOutputStream) {
        gameDaoFileList().forEach { imageFile ->
            zip.putNextEntry(ZipEntry("${GameVaultLibraryFormat.Archive.ImagesPrefix}${imageFile.name}"))
            imageFile.inputStream().use { input -> input.copyTo(zip) }
            zip.closeEntry()
        }
    }

    private fun gameDaoFileList(): List<File> =
        runCatching {
            context.filesDir.resolve(GameVaultLibraryFormat.Archive.ImagesDirectory)
                .listFiles()
                .orEmpty()
                .filter { it.isFile }
        }.getOrDefault(emptyList())

    private fun exportDatabaseCopy(zip: ZipOutputStream) {
        runCatching {
            database.openHelper.writableDatabase.query("PRAGMA wal_checkpoint(FULL)").use { it.moveToFirst() }
        }
        val databaseFile = context.getDatabasePath("game_vault.db")
        listOf(
            databaseFile,
            File("${databaseFile.absolutePath}-wal"),
            File("${databaseFile.absolutePath}-shm"),
        ).filter { it.isFile }.forEach { file ->
            zip.putNextEntry(ZipEntry("${GameVaultLibraryFormat.Archive.DatabasePrefix}${file.name}"))
            file.inputStream().use { input -> input.copyTo(zip) }
            zip.closeEntry()
        }
    }

    private fun writeBackupFile(
        fileName: String,
        mimeType: String,
        writer: (java.io.OutputStream) -> Unit,
    ) {
        val settings = settingsRepository.settings.value
        val treeUri = settings.backupDirectoryUri
            ?.takeIf(::hasPersistedWritePermission)
            ?.let(Uri::parse)
        if (treeUri != null) {
            val documentUri = createDocumentInTree(treeUri, mimeType, fileName)
            context.contentResolver.openOutputStream(documentUri, "w").use { output ->
                requireNotNull(output) { "Не удалось открыть файл для записи." }
                writer(output)
            }
            return
        }

        val outputDir = File(
            Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS),
            "GameVault",
        )
        val canWriteDirectly = Build.VERSION.SDK_INT < Build.VERSION_CODES.R ||
            Environment.isExternalStorageManager()
        require(canWriteDirectly) {
            "Выберите папку резервных копий в настройках или выдайте доступ ко всему накопителю."
        }
        outputDir.mkdirs()
        FileOutputStream(File(outputDir, fileName)).use(writer)
    }

    private fun hasPersistedWritePermission(uri: String): Boolean {
        val parsedUri = runCatching { Uri.parse(uri) }.getOrNull() ?: return false
        return context.contentResolver.persistedUriPermissions.any { permission ->
            permission.uri == parsedUri && permission.isWritePermission
        }
    }

    private fun createDocumentInTree(treeUri: Uri, mimeType: String, fileName: String): Uri {
        val treeDocumentId = DocumentsContract.getTreeDocumentId(treeUri)
        val parentUri = DocumentsContract.buildDocumentUriUsingTree(treeUri, treeDocumentId)
        return requireNotNull(
            DocumentsContract.createDocument(context.contentResolver, parentUri, mimeType, fileName),
        ) { "Не удалось создать файл в выбранной папке." }
    }

    private fun parseImport(uri: Uri): ParsedImport {
        val displayName = queryDisplayName(uri)
        val mimeType = context.contentResolver.getType(uri).orEmpty()
        return parseImport(
            displayName = displayName,
            mimeType = mimeType,
            inputProvider = {
                requireNotNull(context.contentResolver.openInputStream(uri)) {
                    "Не удалось открыть файл импорта."
                }
            },
        )
    }

    private fun parseImport(
        displayName: String,
        mimeType: String,
        inputProvider: () -> InputStream,
    ): ParsedImport {
        val normalizedName = displayName.lowercase()
        return if (normalizedName.endsWith(".zip") || mimeType == GameVaultLibraryFormat.ZipMimeType) {
            parseZipImport(inputProvider)
        } else {
            val json = inputProvider().use { input ->
                JSONObject(input.bufferedReader().readText())
            }
            validateLibraryJson(json)
            ParsedImport(json = json, imageFiles = emptyMap(), tempDir = null)
        }
    }

    private fun parseZipImport(inputProvider: () -> InputStream): ParsedImport {
        val tempDir = File(context.cacheDir, "import-${System.currentTimeMillis()}").also { it.mkdirs() }
        var libraryJson: JSONObject? = null
        val images = mutableMapOf<String, File>()
        inputProvider().use { input ->
            ZipInputStream(input).use { zip ->
                generateSequence { zip.nextEntry }.forEach { entry ->
                    when {
                        entry.isDirectory -> Unit
                        entry.name == GameVaultLibraryFormat.Archive.LibraryJson -> {
                            libraryJson = JSONObject(zip.readEntryText())
                        }
                        entry.name.startsWith(GameVaultLibraryFormat.Archive.ImagesPrefix) -> {
                            val safeName = File(entry.name).name.takeIf { it.isNotBlank() } ?: return@forEach
                            val outputFile = File(tempDir, safeName)
                            outputFile.outputStream().use { output -> zip.copyTo(output) }
                            images[safeName] = outputFile
                        }
                    }
                    zip.closeEntry()
                }
            }
        }
        val json = requireNotNull(libraryJson) { "В архиве не найден library.json." }
        validateLibraryJson(json)
        return ParsedImport(
            json = json,
            imageFiles = images,
            tempDir = tempDir,
        )
    }

    private fun validateLibraryJson(json: JSONObject) {
        val format = json.optString(GameVaultLibraryFormat.Field.Format)
        val version = json.optInt(GameVaultLibraryFormat.Field.Version, -1)
        require(GameVaultLibraryFormat.isSupported(format, version)) {
            "Unsupported backup format/version: $format v$version"
        }
    }

    private fun ZipInputStream.readEntryText(): String {
        val output = ByteArrayOutputStream()
        copyTo(output)
        return output.toString(Charsets.UTF_8.name())
    }

    private fun queryDisplayName(uri: Uri): String {
        val projection = arrayOf(android.provider.OpenableColumns.DISPLAY_NAME)
        context.contentResolver.query(uri, projection, null, null, null).use { cursor ->
            if (cursor != null && cursor.moveToFirst()) {
                return cursor.getString(0).orEmpty()
            }
        }
        return uri.lastPathSegment.orEmpty()
    }

    private suspend fun importPcServices(items: JSONArray): Map<String, String> {
        val idMap = mutableMapOf<String, String>()
        repeat(items.length()) { index ->
            val entity = pcServiceFromJson(items.getJSONObject(index))
            val existing = pcServiceDao.findById(entity.id) ?: pcServiceDao.findByName(entity.name)
            if (existing != null) {
                idMap[entity.id] = existing.id
            } else {
                pcServiceDao.insert(entity)
                idMap[entity.id] = entity.id
            }
        }
        return idMap
    }

    private suspend fun importConsoleFamilies(items: JSONArray): Map<String, String> {
        val idMap = mutableMapOf<String, String>()
        repeat(items.length()) { index ->
            val entity = consoleFamilyFromJson(items.getJSONObject(index))
            val existing = consoleFamilyDao.findById(entity.id) ?: consoleFamilyDao.findByName(entity.name)
            if (existing != null) {
                idMap[entity.id] = existing.id
            } else {
                consoleFamilyDao.insert(entity)
                idMap[entity.id] = entity.id
            }
        }
        return idMap
    }

    private suspend fun importConsoleModels(
        items: JSONArray,
        familyIds: Map<String, String>,
    ): Map<String, String> {
        val idMap = mutableMapOf<String, String>()
        repeat(items.length()) { index ->
            val imported = consoleModelFromJson(items.getJSONObject(index))
            val entity = imported.copy(familyId = familyIds[imported.familyId] ?: imported.familyId)
            val existing = consoleModelDao.findById(entity.id)
                ?: consoleModelDao.findByFamilyAndName(entity.familyId, entity.name)
            if (existing != null) {
                idMap[imported.id] = existing.id
            } else {
                consoleModelDao.insert(entity)
                idMap[imported.id] = entity.id
            }
        }
        return idMap
    }

    private suspend fun importGames(
        games: JSONArray,
        pcServiceIds: Map<String, String>,
        familyIds: Map<String, String>,
        modelIds: Map<String, String>,
        imageFiles: Map<String, File>,
        resolveConflict: suspend (ImportConflict) -> ImportConflictDecision,
    ): ImportCounters {
        var replaceAll = false
        var added = 0
        var replaced = 0
        var skipped = 0

        repeat(games.length()) { index ->
            val raw = games.getJSONObject(index)
            val imageArchiveName = raw.optNullableString(GameVaultLibraryFormat.Field.ImageArchiveName)
            val imported = gameFromJson(raw).copy(
                pcServiceId = remap(
                    raw.optNullableString(GameVaultLibraryFormat.Field.PcServiceId),
                    pcServiceIds,
                ),
                consoleFamilyId = remap(
                    raw.optNullableString(GameVaultLibraryFormat.Field.ConsoleFamilyId),
                    familyIds,
                ),
                consoleModelId = remap(
                    raw.optNullableString(GameVaultLibraryFormat.Field.ConsoleModelId),
                    modelIds,
                ),
            )
            val existing = gameDao.findById(imported.id)
                ?: gameDao.findMatching(imported.title, imported.year, imported.platformType)
            if (existing == null) {
                val importedImages = copyImportedImages(imageArchiveName, imported.imageGallery, imageFiles)
                gameDao.upsert(
                    imported.copy(
                        imageLocalPath = importedImages.coverLocalPath,
                        imageGallery = importedImages.gallery,
                    ),
                )
                added += 1
                return@repeat
            }

            val decision = if (replaceAll) {
                ImportConflictDecision.Replace
            } else {
                resolveConflict(
                    ImportConflict(
                        existing = existing.toDomain(),
                        incoming = imported.toDomain(),
                    ),
                )
            }
            when (decision) {
                ImportConflictDecision.Replace -> {
                    val importedImages = copyImportedImages(imageArchiveName, imported.imageGallery, imageFiles)
                    val imageGallery = importedImages.gallery.ifEmpty { existing.imageGallery }
                    val imageLocalPath = importedImages.coverLocalPath
                        ?: imageGallery.firstOrNull()?.localPath
                        ?: existing.imageLocalPath
                    gameDao.upsert(
                        imported.copy(
                            id = existing.id,
                            imageLocalPath = imageLocalPath,
                            imageGallery = imageGallery,
                        ),
                    )
                    replaced += 1
                }
                ImportConflictDecision.ReplaceAll -> {
                    replaceAll = true
                    val importedImages = copyImportedImages(imageArchiveName, imported.imageGallery, imageFiles)
                    val imageGallery = importedImages.gallery.ifEmpty { existing.imageGallery }
                    val imageLocalPath = importedImages.coverLocalPath
                        ?: imageGallery.firstOrNull()?.localPath
                        ?: existing.imageLocalPath
                    gameDao.upsert(
                        imported.copy(
                            id = existing.id,
                            imageLocalPath = imageLocalPath,
                            imageGallery = imageGallery,
                        ),
                    )
                    replaced += 1
                }
                ImportConflictDecision.Skip -> skipped += 1
                ImportConflictDecision.Cancel -> return ImportCounters(
                    added = added,
                    replaced = replaced,
                    skipped = skipped,
                    canceled = true,
                )
            }
        }

        return ImportCounters(added = added, replaced = replaced, skipped = skipped)
    }

    private fun remap(id: String?, map: Map<String, String>): String? =
        id?.let { map[it] ?: it }

    private fun copyImportedImage(imageArchiveName: String?, imageFiles: Map<String, File>): String? {
        val source = imageArchiveName?.let(imageFiles::get) ?: return null
        val extension = source.extension.ifBlank { "jpg" }
        val imageDir = File(context.filesDir, GameVaultLibraryFormat.Archive.ImagesDirectory).also { it.mkdirs() }
        val outputFile = File(imageDir, "game_${UUID.randomUUID()}.$extension")
        source.inputStream().use { input ->
            outputFile.outputStream().use { output -> input.copyTo(output) }
        }
        return outputFile.absolutePath
    }

    private fun copyImportedImages(
        coverArchiveName: String?,
        images: List<DbStoredImage>,
        imageFiles: Map<String, File>,
    ): ImportedImages {
        val copiedGallery = copyImportedGallery(images, imageFiles)
        val coverFromGallery = coverArchiveName?.let { archiveName ->
            copiedGallery.firstOrNull { File(it.sourceUrl.orEmpty()).name == archiveName || File(it.localPath).name == archiveName }
                ?: images.zip(copiedGallery)
                    .firstOrNull { (original, _) -> File(original.localPath).name == archiveName }
                    ?.second
        }
        val coverLocalPath = coverFromGallery?.localPath
            ?: copyImportedImage(coverArchiveName, imageFiles)
            ?: copiedGallery.firstOrNull()?.localPath
        return ImportedImages(
            coverLocalPath = coverLocalPath,
            gallery = copiedGallery,
        )
    }

    private fun copyImportedGallery(images: List<DbStoredImage>, imageFiles: Map<String, File>): List<DbStoredImage> =
        images.mapNotNull { image ->
            val archiveName = File(image.localPath).name
            copyImportedImage(archiveName, imageFiles)?.let { copiedPath ->
                image.copy(localPath = copiedPath)
            }
        }

    private fun pcServiceFromJson(json: JSONObject): PcServiceEntity =
        PcServiceEntity(
            id = json.getString(GameVaultLibraryFormat.Field.Id),
            name = json.getString(GameVaultLibraryFormat.Field.Name),
            isDefault = json.optBoolean(GameVaultLibraryFormat.Field.IsDefault, false),
        )

    private fun consoleFamilyFromJson(json: JSONObject): ConsoleFamilyEntity =
        ConsoleFamilyEntity(
            id = json.getString(GameVaultLibraryFormat.Field.Id),
            name = json.getString(GameVaultLibraryFormat.Field.Name),
            isDefault = json.optBoolean(GameVaultLibraryFormat.Field.IsDefault, false),
        )

    private fun consoleModelFromJson(json: JSONObject): ConsoleModelEntity =
        ConsoleModelEntity(
            id = json.getString(GameVaultLibraryFormat.Field.Id),
            familyId = json.getString(GameVaultLibraryFormat.Field.FamilyId),
            name = json.getString(GameVaultLibraryFormat.Field.Name),
            isDefault = json.optBoolean(GameVaultLibraryFormat.Field.IsDefault, false),
        )

    private fun gameFromJson(json: JSONObject): GameEntity =
        GameEntity(
            id = json.getString(GameVaultLibraryFormat.Field.Id),
            title = json.getString(GameVaultLibraryFormat.Field.Title),
            year = if (json.isNull(GameVaultLibraryFormat.Field.Year)) null else json.optInt(GameVaultLibraryFormat.Field.Year),
            statuses = json.statusesFromJson(),
            platformType = enumValueOrDefault(json.optString(GameVaultLibraryFormat.Field.PlatformType), DbPlatformType.PC),
            pcServiceId = json.optNullableString(GameVaultLibraryFormat.Field.PcServiceId),
            consoleFamilyId = json.optNullableString(GameVaultLibraryFormat.Field.ConsoleFamilyId),
            consoleModelId = json.optNullableString(GameVaultLibraryFormat.Field.ConsoleModelId),
            imageLocalPath = null,
            imageSourceUrl = json.optNullableString(GameVaultLibraryFormat.Field.ImageSourceUrl),
            imageSourceType = enumValueOrDefault(
                json.optString(GameVaultLibraryFormat.Field.ImageSourceType),
                DbImageSourceType.NONE,
            ),
            imageScale = json.optDouble(GameVaultLibraryFormat.Field.ImageScale, 1.0).toFloat().coerceIn(1f, 4f),
            imageOffsetX = json.optDouble(GameVaultLibraryFormat.Field.ImageOffsetX, 0.0).toFloat().coerceIn(-2f, 2f),
            imageOffsetY = json.optDouble(GameVaultLibraryFormat.Field.ImageOffsetY, 0.0).toFloat().coerceIn(-2f, 2f),
            imageGallery = json.optJSONArray(GameVaultLibraryFormat.Field.ImageGallery).toStoredImages(),
            sourcePageUrl = json.optNullableString(GameVaultLibraryFormat.Field.SourcePageUrl),
            customNotes = json.optJSONArray(GameVaultLibraryFormat.Field.CustomNotes).toGameNotes(),
            createdAt = json.optLong(GameVaultLibraryFormat.Field.CreatedAt, System.currentTimeMillis()),
            updatedAt = json.optLong(GameVaultLibraryFormat.Field.UpdatedAt, System.currentTimeMillis()),
        )

    private inline fun <reified T : Enum<T>> enumValueOrDefault(value: String, default: T): T =
        enumValues<T>().firstOrNull { it.name == value } ?: default

    private fun JSONObject.statusesFromJson(): List<DbGameStatus> {
        val statuses = optJSONArray(GameVaultLibraryFormat.Field.Statuses)?.let { array ->
            List(array.length()) { index ->
                enumValueOrDefault(array.optString(index), DbGameStatus.PLANNED)
            }
        } ?: listOf(enumValueOrDefault(optString(GameVaultLibraryFormat.Field.Status), DbGameStatus.PLANNED))
        return statuses.distinct().ifEmpty { listOf(DbGameStatus.PLANNED) }
    }

    private fun JSONArray?.toGameNotes(): List<DbGameNote> {
        if (this == null) return emptyList()
        return List(length()) { index ->
            val item = optJSONObject(index) ?: JSONObject()
            DbGameNote(
                category = item.optString(GameVaultLibraryFormat.Field.Category),
                text = item.optString(GameVaultLibraryFormat.Field.Text),
            )
        }.filter { it.text.isNotBlank() }
    }

    private fun JSONArray?.toStoredImages(): List<DbStoredImage> {
        if (this == null) return emptyList()
        return List(length()) { index ->
            val item = optJSONObject(index) ?: JSONObject()
            DbStoredImage(
                localPath = item.optString(GameVaultLibraryFormat.Field.LocalPath),
                sourceUrl = item.optNullableString(GameVaultLibraryFormat.Field.SourceUrl),
                sourceType = enumValueOrDefault(
                    item.optString(GameVaultLibraryFormat.Field.SourceType),
                    DbImageSourceType.NONE,
                ),
            )
        }.filter { it.localPath.isNotBlank() }
    }

    private fun JSONObject.optNullableString(name: String): String? =
        if (isNull(name)) null else optString(name).takeIf { it.isNotBlank() }

    private fun timestamp(): String =
        SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(Date())

    private data class ParsedImport(
        val json: JSONObject,
        val imageFiles: Map<String, File>,
        val tempDir: File?,
    )

    private data class ImportCounters(
        val added: Int,
        val replaced: Int,
        val skipped: Int,
        val canceled: Boolean = false,
    )

    private data class ImportedImages(
        val coverLocalPath: String?,
        val gallery: List<DbStoredImage>,
    )
}
