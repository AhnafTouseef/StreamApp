using Microsoft.EntityFrameworkCore;

namespace StreamApp.Data;

/// <summary>
/// Brings a database created by an earlier version up to date WITHOUT touching existing data: only new tables, new nullable/defaulted columns
/// and new indexes are added, each guarded by an existence check, so running it again (or on a freshly created database) changes nothing.
/// (The schema is created with EnsureCreated, which never alters tables that already exist.)
/// </summary>
public static class SchemaUpgrade
{
    public static async Task ApplyAsync(AppDbContext db, ILogger log, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

        var changed = new List<string>();

        async Task<bool> TableExists(string table)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
            var p = c.CreateParameter(); p.ParameterName = "$n"; p.Value = table; c.Parameters.Add(p);
            return await c.ExecuteScalarAsync(ct) != null;
        }
        async Task<bool> ColumnExists(string table, string column)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        async Task Run(string sql)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = sql;
            await c.ExecuteNonQueryAsync(ct);
        }
        async Task EnsureTable(string table, string createSql)
        {
            if (await TableExists(table)) return;
            await Run(createSql);
            changed.Add("table " + table);
        }
        async Task EnsureColumn(string table, string column, string definition)
        {
            if (await ColumnExists(table, column)) return;
            await Run($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}");
            changed.Add($"{table}.{column}");
        }

        // ---- series tables (Series shares its primary key with the catalog entry in Movies)
        await EnsureTable("Series", """
            CREATE TABLE "Series" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Series" PRIMARY KEY,
                "Status" TEXT NULL,
                "Networks" TEXT NULL,
                "LastAirDate" TEXT NULL,
                "TmdbSeasonCount" INTEGER NULL,
                "TmdbEpisodeCount" INTEGER NULL,
                "LastActivityAt" TEXT NULL,
                CONSTRAINT "FK_Series_Movies_Id" FOREIGN KEY ("Id") REFERENCES "Movies" ("Id") ON DELETE CASCADE
            )
            """);
        await EnsureTable("Seasons", """
            CREATE TABLE "Seasons" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Seasons" PRIMARY KEY AUTOINCREMENT,
                "SeriesId" INTEGER NOT NULL,
                "Number" INTEGER NOT NULL,
                "Name" TEXT NULL,
                "Overview" TEXT NULL,
                "PosterUrl" TEXT NULL,
                "AirDate" TEXT NULL,
                "TmdbSeasonId" INTEGER NULL,
                "FolderName" TEXT NULL,
                "FolderUrl" TEXT NULL,
                "FolderLastModified" TEXT NULL,
                CONSTRAINT "FK_Seasons_Series_SeriesId" FOREIGN KEY ("SeriesId") REFERENCES "Series" ("Id") ON DELETE CASCADE
            )
            """);
        await EnsureTable("Episodes", """
            CREATE TABLE "Episodes" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Episodes" PRIMARY KEY AUTOINCREMENT,
                "SeasonId" INTEGER NOT NULL,
                "Number" INTEGER NOT NULL,
                "EndNumber" INTEGER NULL,
                "Title" TEXT NULL,
                "Overview" TEXT NULL,
                "AirDate" TEXT NULL,
                "Runtime" INTEGER NULL,
                "StillUrl" TEXT NULL,
                "TmdbRating" REAL NULL,
                "NumberingInferred" INTEGER NOT NULL DEFAULT 0,
                "CreatedAt" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                CONSTRAINT "FK_Episodes_Seasons_SeasonId" FOREIGN KEY ("SeasonId") REFERENCES "Seasons" ("Id") ON DELETE CASCADE
            )
            """);

        // ---- new columns on existing tables (defaults keep every existing row valid: all of them are movies)
        await EnsureColumn("Sources", "ContentType", "TEXT NOT NULL DEFAULT 'Automatic'");
        await EnsureColumn("Movies", "ContentType", "TEXT NOT NULL DEFAULT 'Movie'");
        await EnsureColumn("MediaFiles", "EpisodeId", "INTEGER NULL REFERENCES \"Episodes\" (\"Id\") ON DELETE CASCADE");
        await EnsureColumn("Subtitles", "EpisodeId", "INTEGER NULL REFERENCES \"Episodes\" (\"Id\") ON DELETE CASCADE");

        // ---- indexes (IF NOT EXISTS makes these idempotent)
        await Run("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Seasons_SeriesId_Number\" ON \"Seasons\" (\"SeriesId\", \"Number\")");
        await Run("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Episodes_SeasonId_Number\" ON \"Episodes\" (\"SeasonId\", \"Number\")");
        await Run("CREATE INDEX IF NOT EXISTS \"IX_MediaFiles_EpisodeId\" ON \"MediaFiles\" (\"EpisodeId\")");
        await Run("CREATE INDEX IF NOT EXISTS \"IX_Subtitles_EpisodeId\" ON \"Subtitles\" (\"EpisodeId\")");
        await Run("CREATE INDEX IF NOT EXISTS \"IX_Movies_ContentType\" ON \"Movies\" (\"ContentType\")");

        if (changed.Count > 0) log.LogInformation("Database upgraded for TV series support (added: {Changes}). Existing data was not modified.", string.Join(", ", changed));
    }
}
