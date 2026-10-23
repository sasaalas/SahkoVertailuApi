
using System.Globalization;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("SahkoVertailu");

builder.Services.AddHttpClient("Sahkonhinta", client =>
{
    client.BaseAddress = new Uri(settings["PalvelunBaseUrl"]!);
    client.Timeout = TimeSpan.FromSeconds(
        settings.GetValue<int>("HttpTimeoutSekuntia"));
    client.DefaultRequestHeaders.UserAgent.ParseAdd(settings["UserAgent"]!);
});

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "Sähkönhinta REST API",
    endpoint = "/api/sahkosopimukset",
    example = "/api/sahkosopimukset"
}));

app.MapGet("/api/sahkosopimukset", async (
    IHttpClientFactory factory,
    IConfiguration configuration,
    string? postinumero = null,
    int? vuosikulutus = null,
    string? hinnoittelumalli = null,
    string? kohderyhma = null,
    CancellationToken cancellationToken = default) =>
{
    var options = configuration.GetSection("SahkoVertailu");
    postinumero ??= options["OletusPostinumero"]!;
    vuosikulutus ??= options.GetValue<int>("OletusVuosikulutusKwh");
    hinnoittelumalli ??= options["Hinnoittelumalli"]!;
    kohderyhma ??= options["Kohderyhma"]!;

    if (postinumero.Length != 5 ||
        !postinumero.All(char.IsDigit))
    {
        return Results.BadRequest(new
        {
            virhe = "Postinumeron pitää sisältää viisi numeroa."
        });
    }

    if (vuosikulutus <= 0 || vuosikulutus > 1000000)
    {
        return Results.BadRequest(new
        {
            virhe = "Vuosikulutuksen pitää olla välillä 1–1000000 kWh."
        });
    }

    if (!hinnoittelumalli.Equals(
        "kiinteä", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new
        {
            virhe = "Tämä API tukee tällä hetkellä kiinteää hinnoittelua."
        });
    }

    if (!kohderyhma.Equals(
        "kotitalous", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new
        {
            virhe = "Tämä API tukee tällä hetkellä kotitalouksia."
        });
    }

    try
    {
        var client = factory.CreateClient("Sahkonhinta");

        using var response = await client.GetAsync(
            "api/productlist/",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);

        using var document =
            await JsonDocument.ParseAsync(stream,
                cancellationToken: cancellationToken);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Results.Problem(
                "Sähkönhinta.fi palautti odottamattoman JSON-rakenteen.",
                statusCode: 502);
        }

        var contracts = new List<object>();

        foreach (var product in document.RootElement.EnumerateArray())
        {
            if (!TryProperty(product, "Details", out var details))
                continue;

            string pricingModel = GetString(details, "PricingModel");
            string targetGroup = GetString(details, "TargetGroup");

            if (pricingModel != "FixedPrice")
                continue;

            if (targetGroup != "Household" &&
                targetGroup != "Both")
                continue;

            if (!TryProperty(details, "Pricing", out var pricing) ||
                !TryProperty(pricing, "PriceComponents", out var components) ||
                components.ValueKind != JsonValueKind.Array)
                continue;

            decimal? energyPrice = null;
            decimal monthlyFee = 0;

            foreach (var component in components.EnumerateArray())
            {
                string type = GetString(component, "PriceComponentType");

                if (!TryProperty(component, "OriginalPayment", out var payment) ||
                    !TryProperty(payment, "Price", out var priceElement) ||
                    !TryGetDecimal(priceElement, out decimal price))
                    continue;

                if (type == "General")
                    energyPrice = price;
                else if (type == "Monthly")
                    monthlyFee = price;
            }

            if (energyPrice is null)
                continue;

            decimal annualCost =
                energyPrice.Value * vuosikulutus.Value / 100m
                + monthlyFee * 12m;

            string company = "";
            if (TryProperty(product, "Company", out var companyElement))
                company = GetString(companyElement, "Name");

            contracts.Add(new
            {
                yhtiö = company,
                nimi = GetString(product, "Name"),
                sopimustyyppi = GetString(details, "ContractType"),
                kesto = GetString(details, "FixedTimeRange"),
                energiahintaSntKwh = energyPrice.Value,
                perusmaksuEurKuukausi = monthlyFee,
                arvioituVuosikustannusEur =
                    Math.Round(annualCost, 2),
                postinumero,
                vuosikulutusKwh = vuosikulutus
            });
        }

        var sorted = contracts
            .OrderBy(item =>
            {
                var property = item.GetType()
                    .GetProperty("arvioituVuosikustannusEur");

                return (decimal)(property!.GetValue(item) ?? 0m);
            })
            .ToList();

        return Results.Ok(new
        {
            haettu = DateTimeOffset.UtcNow,
            hakuehdot = new
            {
                postinumero,
                vuosikulutusKwh = vuosikulutus,
                hinnoittelumalli,
                kohderyhma,
                sopimuksenKesto = options["SopimuksenKesto"]
            },
            huomautus =
                "Postinumeron alueellista saatavuutta ei ole varmistettu. " +
                "Hinnat eivät sisällä sähkönsiirtoa.",
            sopimuksia = sorted.Count,
            tulokset = sorted
        });
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            "Sähkönhinta.fi-palveluun ei saatu yhteyttä.",
            statusCode: 502);
    }
    catch (TaskCanceledException)
    {
        return Results.Problem(
            "Sähkönhinta.fi-kysely aikakatkaistiin.",
            statusCode: 504);
    }
    catch (JsonException)
    {
        return Results.Problem(
            "Palvelun palauttamaa JSON-vastausta ei voitu lukea.",
            statusCode: 502);
    }
});

app.Run();

static bool TryProperty(
    JsonElement element, string name, out JsonElement value)
{
    if (element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out value))
        return true;

    value = default;
    return false;
}

static string GetString(JsonElement element, string property)
{
    if (!TryProperty(element, property, out var value))
        return "";

    return value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? ""
        : value.ToString();
}

static bool TryGetDecimal(JsonElement element, out decimal result)
{
    if (element.ValueKind == JsonValueKind.Number)
        return element.TryGetDecimal(out result);

    return decimal.TryParse(
        element.ToString(),
        NumberStyles.Any,
        CultureInfo.InvariantCulture,
        out result);
}
