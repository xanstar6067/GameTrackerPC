# GameVault Export Format v1

This document is the handoff contract for Android and future desktop clients.
Desktop work must live outside the Android project; this repository only owns the
Android implementation and the shared file format description.

## Archive

The portable backup format is a ZIP archive:

- `library.json` is required and contains the library data.
- `images/` is optional and contains cover/gallery image files referenced by name.
- `database/` is Android recovery data only. Desktop clients must ignore it.

Plain `.json` files are also accepted, but they cannot carry images.

## Root JSON

`library.json` uses UTF-8 JSON with this root shape:

```json
{
  "format": "gamevault-library",
  "version": 1,
  "createdAt": 1710000000000,
  "pcServices": [],
  "consoleFamilies": [],
  "consoleModels": [],
  "themes": {},
  "games": []
}
```

`createdAt`, game `createdAt`, and game `updatedAt` are Unix timestamps in
milliseconds. Unknown future fields should be ignored by importers.

## Games

Each game keeps stable IDs across platforms. Conflict matching is:

1. match by `id`;
2. if no ID match, treat matching `title + year + platformType` as a possible conflict;
3. ask the user to replace, replace all, skip, or cancel.

Portable image rules:

- `imageArchiveName` points to a file inside `images/` and is the portable cover reference.
- `imageGallery[].localPath` may be a platform-local path; importers should use only its file name to match `images/<fileName>`.
- `imageLocalPath` is local cache metadata and must not be trusted across platforms.

## Stable Enum Values

Platform values:

- `PC`
- `CONSOLE`
- `MOBILE`

Game status values:

- `COMPLETED`
- `IN_PROGRESS`
- `POSTPONED`
- `DROPPED`
- `PLANNED`
- `NEVER_PLAY_AGAIN`

Image source values:

- `NONE`
- `GALLERY`
- `DIRECT_IMAGE_URL`
- `AUTO_PARSED`

## Desktop Handoff Prompt

Use this in a separate desktop-client chat:

```text
Build the desktop side against GameVault Export Format v1 from docs/export-format.md.
The portable format is ZIP with library.json plus optional images/. Do not read
Android Room/SQLite database files except as ignored recovery data. Implement
import/export adapters that preserve stable IDs, timestamps in Unix milliseconds,
the listed enum names, and conflict handling by asking the user when IDs or
title+year+platformType collide.
```
