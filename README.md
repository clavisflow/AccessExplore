# AccessDoctor

AccessDoctor is a Blazor WebAssembly application for inspecting the structure of
Microsoft Access `.accdb` / `.mdb` files in the browser.

The app uses WebAssembly builds generated from MDB Tools to read Access file
metadata without requiring Microsoft Access or a server-side upload.

## License

AccessDoctor is distributed under the GNU General Public License version 2.0
only.

See [LICENSE](LICENSE).

## MDB Tools

This repository includes generated WebAssembly artifacts from
[MDB Tools](https://github.com/mdbtools/mdbtools) under `wwwroot/mdbtools/`.

MDB Tools' utilities are licensed under the GNU General Public License, and
MDB Tools' libraries are licensed under the GNU Library General Public License.
Because this application distributes WebAssembly builds generated from MDB
Tools utilities, AccessDoctor is distributed under GPL-2.0-only.

Additional notices are available in:

- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
- [COPYING.LIB.MDBTOOLS](COPYING.LIB.MDBTOOLS)
- [SOURCE_OFFER.md](SOURCE_OFFER.md)

## Rebuilding MDB Tools WebAssembly artifacts

The MDB Tools WebAssembly artifacts can be rebuilt from source with:

```powershell
.\tools\build-mdbtools-wasm.ps1
```

The script uses Docker and the `emscripten/emsdk` image, clones MDB Tools, checks
out the pinned source commit, applies the local `mdb-json --limit` patch, and
writes generated artifacts to `.spike/wasm-artifacts`.

See [SOURCE_OFFER.md](SOURCE_OFFER.md) for complete source and rebuild
information.

## Development

Restore and build:

```powershell
dotnet restore
dotnet build
```

Run locally:

```powershell
dotnet run --urls http://localhost:5178
```
