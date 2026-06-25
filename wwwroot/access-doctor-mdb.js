(function () {
    const filePath = "/work/input.accdb";
    const wasmBasePath = "mdbtools/";
    let currentBytes = null;

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

    async function runCommand(command, bytes, args, options) {
        const stdout = [];
        const stderr = [];
        const startedAt = performance.now();
        const factoryName = commandMap[command];
        const factory = globalThis[factoryName];
        if (typeof factory !== "function") {
            throw new Error(`${factoryName} is not loaded.`);
        }

        const module = await factory({
            print: value => {
                const text = String(value);
                if (typeof options?.onStdout === "function") {
                    options.onStdout(text);
                } else {
                    stdout.push(text);
                }
            },
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

    function ensureCurrentBytes() {
        if (!currentBytes) {
            throw new Error("Access file data is not loaded. Please analyze a file first.");
        }

        return currentBytes;
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

    function parseJsonRows(output) {
        return output
            .map(line => {
                try {
                    return JSON.parse(line);
                } catch {
                    return null;
                }
            })
            .filter(row => row && typeof row === "object" && !Array.isArray(row));
    }

    function displayValue(value) {
        if (value === null || value === undefined) {
            return "";
        }

        if (typeof value === "object") {
            if (typeof value.$binary === "string") {
                return `[binary ${value.$binary.length} chars]`;
            }

            return JSON.stringify(value);
        }

        return String(value);
    }

    function collectColumns(rows) {
        const columns = [];
        const seen = new Set();
        for (const row of rows) {
            for (const column of Object.keys(row)) {
                if (!seen.has(column)) {
                    seen.add(column);
                    columns.push(column);
                }
            }
        }

        return columns;
    }

    function toPreviewRows(rows) {
        return rows.map(row => Object.fromEntries(
            Object.entries(row).map(([key, value]) => [key, displayValue(value)])));
    }

    function csvValue(value) {
        const text = displayValue(value);
        return /[",\r\n]/.test(text)
            ? `"${text.replaceAll("\"", "\"\"")}"`
            : text;
    }

    function downloadBlob(fileName, content, type) {
        const blob = new Blob([content], { type });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement("a");
        anchor.href = url;
        anchor.download = fileName;
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        URL.revokeObjectURL(url);
    }

    window.accessDoctorMdb = {
        async analyzeAccessFile(fileBytes, options) {
            const bytes = toUint8Array(fileBytes);
            currentBytes = bytes;
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

        async readTablePreview(tableName, displayLimit) {
            const bytes = ensureCurrentBytes();
            const maxRows = Math.max(1, Number(displayLimit ?? 50));
            const commandLimit = maxRows + 1;
            const result = await runCommand("mdb-json", bytes, ["--limit", String(commandLimit), "{file}", tableName]);
            const parsedRows = parseJsonRows(result.stdout);
            const previewRows = parsedRows.slice(0, maxRows);

            return {
                columns: collectColumns(previewRows),
                rows: toPreviewRows(previewRows),
                displayLimit: maxRows,
                isTruncated: parsedRows.length > maxRows,
                diagnostic: {
                    command: result.command,
                    elapsedMs: result.elapsedMs,
                    stderr: result.stderr
                }
            };
        },

        async downloadTableCsv(fileName, tableName, columns) {
            const bytes = ensureCurrentBytes();
            const columnNames = Array.isArray(columns)
                ? columns.map(column => String(column)).filter(column => column.length > 0)
                : [];
            const csvLines = [];

            if (columnNames.length > 0) {
                csvLines.push(columnNames.map(csvValue).join(","));

                await runCommand("mdb-json", bytes, ["{file}", tableName], {
                    onStdout: line => {
                        const row = JSON.parse(line);
                        csvLines.push(columnNames.map(column => csvValue(row[column])).join(","));
                    }
                });
            } else {
                const result = await runCommand("mdb-json", bytes, ["{file}", tableName]);
                const rows = parseJsonRows(result.stdout);
                const inferredColumns = collectColumns(rows);
                csvLines.push(inferredColumns.map(csvValue).join(","));
                for (const row of rows) {
                    csvLines.push(inferredColumns.map(column => csvValue(row[column])).join(","));
                }
            }

            downloadBlob(fileName, `\uFEFF${csvLines.join("\r\n")}\r\n`, "text/csv;charset=utf-8");
        },

        downloadJson(fileName, data) {
            downloadBlob(fileName, JSON.stringify(data, null, 2), "application/json;charset=utf-8");
        }
    };

    document.documentElement.dataset.accessDoctorMdb = "ready";
    document.documentElement.dataset.accessDoctorMdbFactories = Object.values(commandMap)
        .every(factoryName => typeof globalThis[factoryName] === "function")
        ? "ready"
        : "missing";
})();
