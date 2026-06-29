# Source code and rebuild information

AccessDoctor is distributed under GPL-2.0-only. The complete corresponding
source for this repository includes:

- the Blazor WebAssembly application source;
- scripts used to build the MDB Tools WebAssembly artifacts;
- the local patch logic applied to MDB Tools;
- documentation needed to reproduce the generated MDB Tools artifacts.

## MDB Tools WebAssembly artifacts

The files in `wwwroot/mdbtools/` are generated binaries and JavaScript wrappers
from MDB Tools.

To rebuild them:

```powershell
.\tools\build-mdbtools-wasm.ps1
```

Build requirements:

- Git
- Docker
- Network access to clone `https://github.com/mdbtools/mdbtools.git`
- The `emscripten/emsdk` Docker image

The current local MDB Tools source commit used during development was:

```text
cc4aa5d953900073d3ba99cf6a3739721e37831f
```

If exact binary reproduction is required, clone MDB Tools at that commit before
running the script or pass a source directory containing that checkout:

```powershell
git clone https://github.com/mdbtools/mdbtools.git .spike\mdbtools
git -C .spike\mdbtools checkout cc4aa5d953900073d3ba99cf6a3739721e37831f
.\tools\build-mdbtools-wasm.ps1
```

The generated files are written to `.spike/wasm-artifacts`. Copy the generated
`mdb-*.js` and `mdb-*.wasm` files into `wwwroot/mdbtools/` for application use.

## Application build

Restore and build the Blazor WebAssembly application:

```powershell
dotnet restore
dotnet build
```

For development:

```powershell
dotnet run --urls http://localhost:5178
```
