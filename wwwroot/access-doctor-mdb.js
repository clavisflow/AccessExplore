(function () {
    const filePath = "/work/input.accdb";
    const wasmBasePath = "mdbtools/";

    const commandMap = {
        "mdb-ver": "createMdbVerModule",
        "mdb-tables": "createMdbTablesModule",
        "mdb-schema": "createMdbSchemaModule",
        "mdb-count": "createMdbCountModule",
        "mdb-queries": "createMdbQueriesModule",
        "mdb-json": "createMdbJsonModule"
    };

    function toUint8Array(value) {
        if (value instanceof Uint8Array) {
            return value;
        }

        if (value instanceof ArrayBuffer) {
            return new Uint8Array(value);
        }

        return Uint8Array.from(value);
    }

    function lines(output) {
        return output
            .map(line => String(line).trim())
            .filter(line => line.length > 0);
    }

    async function runCommand(command, bytes, args) {
        const stdout = [];
        const stderr = [];
        const startedAt = performance.now();
        const factoryName = commandMap[command];
        const factory = globalThis[factoryName];
        if (typeof factory !== "function") {
            throw new Error(`${factoryName} is not loaded.`);
        }

        const module = await factory({
            print: value => stdout.push(String(value)),
            printErr: value => stderr.push(String(value)),
            locateFile: fileName => `${wasmBasePath}${fileName}`
        });

        try {
            module.FS.mkdir("/work");
        } catch {
            // The directory can already exist in the module instance.
        }

        module.FS.writeFile(filePath, bytes);

        const commandArgs = args.includes("{file}")
            ? args.map(arg => arg === "{file}" ? filePath : arg)
            : [...args, filePath];

        try {
            module.callMain(commandArgs);
        } catch {
            // Emscripten uses exceptions for process exits. stderr carries command failures.
        }

        return {
            command,
            elapsedMs: Math.round(performance.now() - startedAt),
            stdout,
            stderr
        };
    }

    async function tableNames(bytes, type) {
        const result = await runCommand("mdb-tables", bytes, ["-1", "-t", type]);
        return { names: lines(result.stdout), diagnostic: result };
    }

    function parseNumber(value) {
        const number = Number(value);
        return Number.isFinite(number) ? number : null;
    }

    function parseObjectCatalog(output) {
        return output
            .map(line => {
                try {
                    const item = JSON.parse(line);
                    const name = String(item.Name ?? "").trim();
                    if (!name) {
                        return null;
                    }

                    return {
                        name,
                        typeCode: parseNumber(item.Type),
                        flags: parseNumber(item.Flags),
                        dateCreate: String(item.DateCreate ?? "").trim(),
                        dateUpdate: String(item.DateUpdate ?? "").trim()
                    };
                } catch {
                    return null;
                }
            })
            .filter(Boolean);
    }

    function hasSystemTablePermissionError(stderrLines) {
        const text = stderrLines.join("\n").toLowerCase();
        return text.includes("permission")
            || text.includes("access is denied")
            || text.includes("not authorized")
            || text.includes("could not open table")
            || text.includes("msysobjects");
    }

    window.accessDoctorMdb = {
        async analyzeAccessFile(fileBytes, options) {
            const bytes = toUint8Array(fileBytes);
            const diagnostics = [];

            const versionResult = await runCommand("mdb-ver", bytes, []);
            diagnostics.push(versionResult);

            const tablesResult = await tableNames(bytes, "table");
            const queriesResult = await tableNames(bytes, "query");
            const formsResult = await tableNames(bytes, "form");
            const reportsResult = await tableNames(bytes, "report");
            const macrosResult = await tableNames(bytes, "macro");
            const modulesResult = await tableNames(bytes, "module");
            const relationshipsResult = await tableNames(bytes, "relationship");
            const linkedTablesResult = await tableNames(bytes, "linkedtable");
            diagnostics.push(
                tablesResult.diagnostic,
                queriesResult.diagnostic,
                formsResult.diagnostic,
                reportsResult.diagnostic,
                macrosResult.diagnostic,
                modulesResult.diagnostic,
                relationshipsResult.diagnostic,
                linkedTablesResult.diagnostic);

            let objectDetails = [];
            const objectCatalogResult = await runCommand("mdb-json", bytes, ["{file}", "MSysObjects"]);
            diagnostics.push(objectCatalogResult);
            if (!hasSystemTablePermissionError(objectCatalogResult.stderr)) {
                objectDetails = parseObjectCatalog(objectCatalogResult.stdout);
            }

            const schemaResult = await runCommand("mdb-schema", bytes, []);
            diagnostics.push(schemaResult);

            const queryListResult = await runCommand("mdb-queries", bytes, ["-1"]);
            diagnostics.push(queryListResult);
            const queryNames = lines(queryListResult.stdout).length > 0
                ? lines(queryListResult.stdout)
                : queriesResult.names;

            const tableCounts = [];
            for (const tableName of tablesResult.names) {
                const countResult = await runCommand("mdb-count", bytes, ["{file}", tableName]);
                diagnostics.push(countResult);
                tableCounts.push({
                    tableName,
                    count: lines(countResult.stdout)[0] ?? "",
                    stderr: countResult.stderr.join("\n")
                });
            }

            const maxQuerySql = Number(options?.maxQuerySql ?? 30);
            const largeFileQuerySql = Number(options?.largeFileQuerySql ?? 5);
            const largeFileBytes = Number(options?.largeFileBytes ?? 104857600);
            const querySqlLimit = bytes.byteLength >= largeFileBytes
                ? Math.min(maxQuerySql, largeFileQuerySql)
                : Math.min(maxQuerySql, queryNames.length);
            const querySql = [];

            for (const queryName of queryNames.slice(0, querySqlLimit)) {
                const sqlResult = await runCommand("mdb-queries", bytes, ["{file}", queryName]);
                diagnostics.push(sqlResult);
                querySql.push({
                    queryName,
                    sql: sqlResult.stdout.join("\n"),
                    stderr: sqlResult.stderr.join("\n")
                });
            }

            return {
                version: lines(versionResult.stdout)[0] ?? "",
                tables: tablesResult.names,
                queries: queryNames,
                forms: formsResult.names,
                reports: reportsResult.names,
                macros: macrosResult.names,
                modules: modulesResult.names,
                relationships: relationshipsResult.names,
                linkedTables: linkedTablesResult.names,
                objectDetails,
                schema: schemaResult.stdout.join("\n"),
                tableCounts,
                querySql,
                commandDiagnostics: diagnostics.map(item => ({
                    command: item.command,
                    elapsedMs: item.elapsedMs,
                    stderr: item.stderr
                }))
            };
        },

        downloadJson(fileName, data) {
            const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json;charset=utf-8" });
            const url = URL.createObjectURL(blob);
            const anchor = document.createElement("a");
            anchor.href = url;
            anchor.download = fileName;
            document.body.appendChild(anchor);
            anchor.click();
            anchor.remove();
            URL.revokeObjectURL(url);
        }
    };

    document.documentElement.dataset.accessDoctorMdb = "ready";
    document.documentElement.dataset.accessDoctorMdbFactories = Object.values(commandMap)
        .every(factoryName => typeof globalThis[factoryName] === "function")
        ? "ready"
        : "missing";
})();
