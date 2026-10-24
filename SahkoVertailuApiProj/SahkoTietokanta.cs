using Microsoft.Data.Sqlite;

public sealed record SahkoSopimus(
    string Yhtio,
    string Nimi,
    string Sopimustyyppi,
    string Kesto,
    decimal EnergiahintaSntKwh,
    decimal PerusmaksuEurKuukausi,
    decimal Vuosikustannus,
    string Postinumero,
    int VuosikulutusKwh);

public sealed class SahkoTietokanta
{
    private readonly string _connectionString;

    public SahkoTietokanta(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS haut (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                haettu_utc TEXT NOT NULL,
                postinumero TEXT NOT NULL,
                vuosikulutus_kwh INTEGER NOT NULL,
                hinnoittelumalli TEXT NOT NULL,
                kohderyhma TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sopimukset (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                haku_id INTEGER NOT NULL,
                yhtio TEXT,
                nimi TEXT,
                sopimustyyppi TEXT,
                kesto TEXT,
                energiahinta_snt_kwh REAL NOT NULL,
                perusmaksu_eur_kuukausi REAL NOT NULL,
                arvioitu_vuosikustannus_eur REAL NOT NULL,
                FOREIGN KEY (haku_id) REFERENCES haut(id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveSearchAsync(
        DateTimeOffset fetchedAt,
        string postalCode,
        int annualConsumptionKwh,
        string pricingModel,
        string targetGroup,
        IReadOnlyCollection<SahkoSopimus> contracts,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var searchCommand = connection.CreateCommand())
        {
            searchCommand.Transaction = (SqliteTransaction)transaction;
            searchCommand.CommandText = """
                INSERT INTO haut
                    (haettu_utc, postinumero, vuosikulutus_kwh, hinnoittelumalli, kohderyhma)
                VALUES
                    ($haettuUtc, $postinumero, $vuosikulutusKwh, $hinnoittelumalli, $kohderyhma);
                SELECT last_insert_rowid();
                """;
            searchCommand.Parameters.AddWithValue("$haettuUtc", fetchedAt.ToString("O"));
            searchCommand.Parameters.AddWithValue("$postinumero", postalCode);
            searchCommand.Parameters.AddWithValue("$vuosikulutusKwh", annualConsumptionKwh);
            searchCommand.Parameters.AddWithValue("$hinnoittelumalli", pricingModel);
            searchCommand.Parameters.AddWithValue("$kohderyhma", targetGroup);
            var result = await searchCommand.ExecuteScalarAsync(cancellationToken);
            var searchId = Convert.ToInt64(result);

            foreach (var contract in contracts)
            {
                await using var contractCommand = connection.CreateCommand();
                contractCommand.Transaction = (SqliteTransaction)transaction;
                contractCommand.CommandText = """
                    INSERT INTO sopimukset
                        (haku_id, yhtio, nimi, sopimustyyppi, kesto,
                         energiahinta_snt_kwh, perusmaksu_eur_kuukausi,
                         arvioitu_vuosikustannus_eur)
                    VALUES
                        ($hakuId, $yhtio, $nimi, $sopimustyyppi, $kesto,
                         $energiahinta, $perusmaksu, $vuosikustannus);
                    """;
                contractCommand.Parameters.AddWithValue("$hakuId", searchId);
                contractCommand.Parameters.AddWithValue("$yhtio", contract.Yhtio);
                contractCommand.Parameters.AddWithValue("$nimi", contract.Nimi);
                contractCommand.Parameters.AddWithValue("$sopimustyyppi", contract.Sopimustyyppi);
                contractCommand.Parameters.AddWithValue("$kesto", contract.Kesto);
                contractCommand.Parameters.AddWithValue("$energiahinta", contract.EnergiahintaSntKwh);
                contractCommand.Parameters.AddWithValue("$perusmaksu", contract.PerusmaksuEurKuukausi);
                contractCommand.Parameters.AddWithValue("$vuosikustannus", contract.Vuosikustannus);
                await contractCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
