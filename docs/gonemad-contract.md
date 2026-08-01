# GoneMAD playlist contract

Validated with GoneMAD on the Android device exposed over Windows MTP as:

```text
MLE S24U\Internal storage\gmmp\playlists
```

Tests use only the disposable `Shmembee Contract Test` playlist. Existing
playlists are read-only references for known valid phone paths.

## Confirmed behavior

- GoneMAD discovers externally copied `.m3u` and `.m3u8` files after a playlist
  rescan.
- An `.m3u8` containing only nonexistent paths appears as a playlist with no
  visible tracks. Unresolved entries therefore cannot be used to test sequence
  behavior.
- A UTF-8 `.m3u` containing real phone-relative paths resolves and plays.
- Input order is preserved.
- Duplicate occurrences are preserved and shown independently.
- Japanese metadata and path characters, accented characters, and curly Unicode
  quotation marks are preserved and resolve correctly.
- After scanning, GoneMAD autosaves the playlist in a normalized form:
  - `#EXTM3U` and `#EXTINF` lines are removed.
  - Paths become absolute Android paths rooted at `/storage/emulated/0/`.
  - Ordering and duplicate occurrences remain intact.
- Edits made in GoneMAD are autosaved to the same M3U file:
  - Moving the final track to the first position changed the persisted order.
  - Removing one duplicate removed exactly one occurrence.
  - Adding a library track appended its absolute Android path.
- An external M3U replacement while GoneMAD is open is accepted after a
  playlist rescan. GoneMAD discarded its previous in-memory four-track result
  and displayed the replacement's three tracks in the supplied order. The
  captured phone file matched the replacement after the rescan.
- Renaming a playlist in GoneMAD changes its display name in GoneMAD's database
  but does not rename the backing M3U file. This behavior was reproduced with
  both a Unicode name (`Shmembee 日本語 é Test`) and an ASCII name; the same
  three tracks remained available, while MTP still exposed
  `Shmembee Contract Test.m3u`.
- Deleting the renamed playlist in GoneMAD removed both the database entry and
  its original `Shmembee Contract Test.m3u` backing file, even though the
  display name no longer matched the filename. The file's absence was verified
  over MTP after reconnecting the phone.
- An external file rename, implemented as delete-old/create-new over MTP, is
  reflected after rescan: only the new filename-derived playlist appears, with
  all three tracks intact. Externally deleting that new file and rescanning
  removes the playlist from GoneMAD.
- A Unicode playlist display name works when renamed inside GoneMAD. Creating a
  Unicode backing filename through the Windows Shell/MTP automation path
  mangled non-ASCII characters (`日本語 é` became `??? �`), so Shmembee must not
  assume that Windows Shell MTP can safely transport Unicode filenames. Keep
  generated backing filenames conservative and store Unicode display names
  separately.
- The GoneMAD developer described the same database-name/file-name split in a
  [forum response](https://gonemadmusicplayer.proboards.com/thread/912/locate-existing-playlist-on-device).

Example normalized output:

```text
/storage/emulated/0/Music/Scandal/Fullmetal Alchemist  Brotherhood Original Soundtrack/# - Shunkan Sentimental.mp3
/storage/emulated/0/Music/Rising of the Shield Hero/Tate no Yuusha no Nariagari OST “Dusk”/1-11 - Kansas.mp3
/storage/emulated/0/Music/Scandal/Fullmetal Alchemist  Brotherhood Original Soundtrack/# - Shunkan Sentimental.mp3
/storage/emulated/0/Music/Hata Motohiro/Kotonoha (言ノ葉)/1-01 - 言ノ葉.mp3
```

## Implementation implications

- Read both `.m3u` and `.m3u8`.
- Write UTF-8 `.m3u` by default to match existing GoneMAD playlists.
- Accept relative phone paths and absolute `/storage/emulated/0/` paths as
  equivalent aliases.
- Ignore M3U metadata comments for identity; GoneMAD may remove them.
- Preserve duplicate occurrences and sequence positions in snapshots and diffs.
- Expect GoneMAD to rewrite an externally supplied file after scanning, so
  compare parsed playlist semantics rather than raw bytes or checksums.
- Treat GoneMAD's display name and backing filename as separate identifiers.
  Do not infer a file rename from an in-app rename.
- Model external rename as delete/create unless a stable sidecar ID proves
  continuity. Model external deletion as a reviewed deletion after rescan.
- Prefer conservative ASCII backing filenames over Windows Shell MTP; Unicode
  playlist display names remain supported inside GoneMAD.

## Remaining contract checks

None for the Phase 0 GoneMAD proof gate.
