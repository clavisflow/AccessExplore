# Third-party notices

This project is distributed under the GNU General Public License version 2.0
only. See [LICENSE](LICENSE).

This notice summarizes third-party software that is intentionally included in
the application distribution or used to generate distributed artifacts.

## MDB Tools

AccessDoctor includes WebAssembly builds generated from MDB Tools.

- Project: MDB Tools
- Upstream repository: https://github.com/mdbtools/mdbtools
- Local source commit used for the current build: `cc4aa5d953900073d3ba99cf6a3739721e37831f`
- Upstream license summary: MDB Tools utilities and GUI are GPL; `libmdb`,
  `libmdbsql`, and `libmdbodbc` are LGPL. See MDB Tools' `COPYING` and
  `COPYING.LIB`.
- GPL license text included in this repository: [LICENSE](LICENSE)
- LGPL license text included for MDB Tools library components:
  [COPYING.LIB.MDBTOOLS](COPYING.LIB.MDBTOOLS)

The distributed files under `wwwroot/mdbtools/` are generated from MDB Tools
utilities and `libmdb`:

- `mdb-ver.js` / `mdb-ver.wasm`
- `mdb-tables.js` / `mdb-tables.wasm`
- `mdb-schema.js` / `mdb-schema.wasm`
- `mdb-count.js` / `mdb-count.wasm`
- `mdb-queries.js` / `mdb-queries.wasm`
- `mdb-json.js` / `mdb-json.wasm`

### Local MDB Tools modification

The build script patches MDB Tools' `src/util/mdb-json.c` before compiling.
The patch adds a `--limit` / `-l` option that stops JSON export after a
specified number of rows. This is used by the browser table preview feature to
avoid loading large tables into the UI by default.

The modification is applied by [tools/build-mdbtools-wasm.ps1](tools/build-mdbtools-wasm.ps1).

### Rebuilding the WebAssembly artifacts

The generated MDB Tools WebAssembly artifacts can be reproduced from source by
running the build script from the repository root:

```powershell
.\tools\build-mdbtools-wasm.ps1
```

The script:

1. Clones MDB Tools from `https://github.com/mdbtools/mdbtools.git` into
   `.spike/mdbtools` when the source directory does not already exist.
2. Applies the local `mdb-json --limit` patch.
3. Builds MDB Tools in the official `emscripten/emsdk` Docker image.
4. Writes generated WebAssembly artifacts to `.spike/wasm-artifacts`.

The application distribution keeps the generated artifacts in
`wwwroot/mdbtools/`.

## Microsoft .NET / Blazor WebAssembly

The application is built with Microsoft .NET and Blazor WebAssembly packages.
Those packages are not vendored as source in this repository; they are restored
through NuGet when building the project. Their licensing is governed by the
corresponding Microsoft package licenses.
