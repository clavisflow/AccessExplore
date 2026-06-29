param(
    [string]$SourceDir = ".spike\mdbtools",
    [string]$OutDir = ".spike\wasm-artifacts",
    [string]$MdbToolsRepository = "https://github.com/mdbtools/mdbtools.git",
    [string]$MdbToolsCommit = "cc4aa5d953900073d3ba99cf6a3739721e37831f",
    [string]$EmscriptenImage = "emscripten/emsdk:latest"
)

$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path (Get-Location) $SourceDir
$outPath = Join-Path (Get-Location) $OutDir

New-Item -ItemType Directory -Force (Split-Path $sourcePath -Parent) | Out-Null
New-Item -ItemType Directory -Force $outPath | Out-Null

if (-not (Test-Path $sourcePath)) {
    git -c core.autocrlf=false clone $MdbToolsRepository $sourcePath
}

if (-not [string]::IsNullOrWhiteSpace($MdbToolsCommit)) {
    git -C $sourcePath fetch origin $MdbToolsCommit
    git -C $sourcePath checkout $MdbToolsCommit
}

$mdbJsonPath = Join-Path $sourcePath "src\util\mdb-json.c"
$mdbJsonSource = Get-Content -Raw $mdbJsonPath
if ($mdbJsonSource -notmatch 'G_OPTION_ARG_INT,\s*&row_limit') {
    $mdbJsonSource = $mdbJsonSource.Replace(
        "	int print_mdbver = 0;",
        "	int print_mdbver = 0;`n	gint row_limit = 0;")

    $mdbJsonSource = $mdbJsonSource.Replace(
        '		{"no-unprintable", ''U'', 0, G_OPTION_ARG_NONE, &drop_nonascii, "Change unprintable characters to spaces (otherwise escaped as \\u00XX)", NULL},',
        '		{"no-unprintable", ''U'', 0, G_OPTION_ARG_NONE, &drop_nonascii, "Change unprintable characters to spaces (otherwise escaped as \\u00XX)", NULL},' + "`n" +
        '		{"limit", ''l'', 0, G_OPTION_ARG_INT, &row_limit, "Maximum number of rows to export. 0 means all rows.", "rows"},')

    $mdbJsonSource = $mdbJsonSource.Replace(
        "		fputs(row_end, outfile);`n	}",
        "		fputs(row_end, outfile);`n`n		if (row_limit > 0 && --row_limit == 0) {`n			break;`n		}`n	}")

    Set-Content -NoNewline -Path $mdbJsonPath -Value $mdbJsonSource
}

$containerCommand = @'
set -e

apt-get update >/tmp/apt-update.log
apt-get install -y autoconf automake libtool pkg-config flex bison gawk gettext autopoint >/tmp/apt-install.log

find . -type f \( -name '*.am' -o -name '*.ac' -o -name '*.m4' -o -name '*.in' -o -name '*.c' -o -name '*.h' -o -name '*.l' -o -name '*.y' \) -print0 | xargs -0 sed -i 's/\r$//'

autoreconf -i -f
emconfigure ./configure \
  --host=wasm32-unknown-emscripten \
  --disable-shared \
  --enable-static \
  --disable-glib \
  --disable-iconv \
  --disable-man \
  --with-bash-completion-dir=no

emmake make -j2

emcc src/util/mdb-tables.o src/libmdb/.libs/libmdb.a \
  -sMODULARIZE=1 -sEXPORT_NAME=createMdbTablesModule \
  -sENVIRONMENT=web,worker,node -sINVOKE_RUN=0 -sALLOW_MEMORY_GROWTH=1 -sFORCE_FILESYSTEM=1 \
  -sEXPORTED_RUNTIME_METHODS=FS,callMain \
  -o /out/mdb-tables.js

emcc src/util/mdb-schema.o src/libmdb/.libs/libmdb.a \
  -sMODULARIZE=1 -sEXPORT_NAME=createMdbSchemaModule \
  -sENVIRONMENT=web,worker,node -sINVOKE_RUN=0 -sALLOW_MEMORY_GROWTH=1 -sFORCE_FILESYSTEM=1 \
  -sEXPORTED_RUNTIME_METHODS=FS,callMain \
  -o /out/mdb-schema.js

emcc src/util/mdb-count.o src/libmdb/.libs/libmdb.a \
  -sMODULARIZE=1 -sEXPORT_NAME=createMdbCountModule \
  -sENVIRONMENT=web,worker,node -sINVOKE_RUN=0 -sALLOW_MEMORY_GROWTH=1 -sFORCE_FILESYSTEM=1 \
  -sEXPORTED_RUNTIME_METHODS=FS,callMain \
  -o /out/mdb-count.js

emcc src/util/mdb-queries.o src/libmdb/.libs/libmdb.a \
  -sMODULARIZE=1 -sEXPORT_NAME=createMdbQueriesModule \
  -sENVIRONMENT=web,worker,node -sINVOKE_RUN=0 -sALLOW_MEMORY_GROWTH=1 -sFORCE_FILESYSTEM=1 \
  -sEXPORTED_RUNTIME_METHODS=FS,callMain \
  -o /out/mdb-queries.js

emcc src/util/mdb-json.o src/libmdb/.libs/libmdb.a \
  -sMODULARIZE=1 -sEXPORT_NAME=createMdbJsonModule \
  -sENVIRONMENT=web,worker,node -sINVOKE_RUN=0 -sALLOW_MEMORY_GROWTH=1 -sFORCE_FILESYSTEM=1 \
  -sEXPORTED_RUNTIME_METHODS=FS,callMain \
  -o /out/mdb-json.js

emcc src/util/mdb-ver.o src/libmdb/.libs/libmdb.a \
  -sMODULARIZE=1 -sEXPORT_NAME=createMdbVerModule \
  -sENVIRONMENT=web,worker,node -sINVOKE_RUN=0 -sALLOW_MEMORY_GROWTH=1 -sFORCE_FILESYSTEM=1 \
  -sEXPORTED_RUNTIME_METHODS=FS,callMain \
  -o /out/mdb-ver.js
'@

docker run --rm `
    -v "${sourcePath}:/src" `
    -v "${outPath}:/out" `
    -w /src `
    $EmscriptenImage `
    bash -lc $containerCommand

Get-ChildItem $outPath | Sort-Object Name | Select-Object Name, Length
