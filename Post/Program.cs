using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

[TestFixture]
public class ApiTests
{
    private CosmosClient _cosmosClient = null!;
    private Database _database = null!;
    private Container _shipmentContainer = null!;
    private Container _shipmentXrefContainer = null!;
    private string _newCorrelationId = null!;
    private string _newOrderCode = null!;
    private JObject _jObject = null!;

    [SetUp]
    public async Task SetUp()
    {
        // Step 1: Connect to Cosmos DB
        string cosmosEndpoint = "https://qa-cosmos-cus-shipment-01.documents.azure.com:443/";
        string cosmosKey = ""; // Replace with your actual primary key
        string databaseId = "Shipment";
        string shipmentContainerId = "Items";
        string shipmentXrefContainerId = "ShipmentXref";

        _cosmosClient = new CosmosClient(cosmosEndpoint, cosmosKey);
        _database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(databaseId);

        // Use the correct partition key path for each container
        _shipmentContainer = await _database.CreateContainerIfNotExistsAsync(new ContainerProperties(shipmentContainerId, "/partitionKey"));
        _shipmentXrefContainer = await _database.CreateContainerIfNotExistsAsync(new ContainerProperties(shipmentXrefContainerId, "/PartitionKey"));

        Console.WriteLine("Connected to Cosmos DB and containers created or verified.");
    }

    [Test]
    public void TestCosmosDbConnection()
    {
        Assert.IsNotNull(_cosmosClient);
        Assert.IsNotNull(_database);
        Assert.IsNotNull(_shipmentContainer);
        Assert.IsNotNull(_shipmentXrefContainer);
        Console.WriteLine("Cosmos DB connection verified.");
    }

    [Test]
    public async Task TestSendPostRequest()
    {
        // Generate new unique values
        _newCorrelationId = GenerateRandomAlphanumeric(16);
        _newOrderCode = GenerateRandomAlphanumeric(17);

        // Initialize Playwright and set up the API request context
        using var playwright = await Playwright.CreateAsync();
        var requestContext = await playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = "https://qa-api.werner.com",
            ExtraHTTPHeaders = new System.Collections.Generic.Dictionary<string, string>
            {
                { "Authorization", "Bearer eyJraWQiOiJ3eTJhejEwNEl6SnlpNDJIMjgxWDAtclpmRTVDbzBJYXh2eW9KWURDMS1FIiwiYWxnIjoiUlMyNTYifQ.eyJ2ZXIiOjEsImp0aSI6IkFULlJHY3Nodnd6T2JfVC03Mmgwemd5Z2JHbTdOY2hWQm50SEIxNmUzVnotaFEiLCJpc3MiOiJodHRwczovL3dlcm5lci1hcGkub2t0YS5jb20vb2F1dGgyL2RlZmF1bHQiLCJhdWQiOiJhcGk6Ly9kZWZhdWx0IiwiaWF0IjoxNzE5MjY5NDg3LCJleHAiOjE3MTkyNzMwODcsImNpZCI6IjBvYTI0bDl1Z2dJNkpSSmNkNjk3Iiwic2NwIjpbImRlZmF1bHRfc2NvcGUiXSwic3ViIjoiMG9hMjRsOXVnZ0k2SlJKY2Q2OTcifQ.KRmxmcVeu36aFjbzqCc3VLTN8MPdpss-i6pQ8HWVv6vUvKMPEfATYkopIejuHzPjlGicRrGzqZOMU1x7ElPhOaqx661kI3c3Vyn6SXTUfIp-IhfUrQ5Ce6ujbXhivpIH5aA9cZsStP3C1n_tx92xuluGT3rnTu9VXHw7C0Dv-oQPB9Fr0_ZjQkB0nOFFbB25QzDq4PGcoS0bak-PRg2YXrauUOv_XWPE6TPlLvceG0MkUK0p5d_Q4jz1JF18MPBehCadk0gVVZ_A12pYdt--tZim-rAZF_3g68SFeuDFr4rfdASrnLNMeHzcE9lwGuGRRyawfHXsmCydaZNehn2HCg" }, // Replace with your valid Bearer token
                { "Content-Type", "application/json" }
            }
        });

        // Read and modify JSON content from the file
        string filePath = Path.Combine(Directory.GetCurrentDirectory(), "Postman.json");
        string json = File.ReadAllText(filePath);
        _jObject = JObject.Parse(json);
        ValidateJsonStructure(_jObject);
        _jObject["correlationId"] = _newCorrelationId;
        _jObject["order"]["code"] = _newOrderCode;
        string updatedJson = _jObject.ToString(Newtonsoft.Json.Formatting.None);

        // Send a POST request with the modified JSON data
        var response = await requestContext.PostAsync("/load-xapi/v2/orders", new APIRequestContextOptions
        {
            Data = updatedJson
        });

        Console.WriteLine($"Status: {response.Status}");
        var body = await response.TextAsync();
        Console.WriteLine($"Body: {body}");

        // Assert that the POST request was successful
        Assert.AreEqual(201, (int)response.Status, "Failed to send POST request");
    }

    [Test]
    public async Task TestQueryCosmosDb()
    {
        // Save the modified JSON data to Cosmos DB
        var item = new
        {
            id = Guid.NewGuid().ToString(),
            partitionKey = "your-partition-key", // Match the partition key name to the container's key
            correlationId = _newCorrelationId,
            orderCode = _newOrderCode,
            content = _jObject
        };

        await _shipmentContainer.CreateItemAsync(item, new PartitionKey("your-partition-key"));
        Console.WriteLine("Item saved to Cosmos DB.");

        // Query the ShipmentXref container to verify the saved item by orderCode
        var queryResult = await QueryShipmentContainerAsync("your-partition-key", _newOrderCode);
        Assert.IsTrue(queryResult, "Failed to find item in Cosmos DB");
    }

    private async Task<bool> QueryShipmentContainerAsync(string partitionKey, string orderCode)
    {
        var sqlQueryText = "SELECT * FROM c WHERE c.partitionKey = @partitionKey AND c.orderCode = @orderCode";
        var queryDefinition = new QueryDefinition(sqlQueryText)
            .WithParameter("@partitionKey", partitionKey)
            .WithParameter("@orderCode", orderCode);

        using FeedIterator<JObject> queryResultSetIterator = _shipmentContainer.GetItemQueryIterator<JObject>(queryDefinition);

        while (queryResultSetIterator.HasMoreResults)
        {
            FeedResponse<JObject> currentResultSet = await queryResultSetIterator.ReadNextAsync();
            foreach (JObject item in currentResultSet)
            {
                Console.WriteLine(item.ToString());
                return true; // Found item in the container
            }
        }
        return false; // Did not find item in the container
    }

    // Helper method to generate random alphanumeric strings
    private static string GenerateRandomAlphanumeric(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }

    // Helper method to validate JSON structure
    private static void ValidateJsonStructure(JObject jObject)
    {
        if (!(jObject["correlationId"] is JValue) ||
            !(jObject["order"] is JObject) ||
            !(jObject["order"]["code"] is JValue) ||
            !(jObject["stops"] is JArray) ||
            !(jObject["routes"] is JArray))
        {
            throw new InvalidOperationException("Unexpected JSON structure.");
        }

        foreach (var stop in jObject["stops"] as JArray)
        {
            if (!(stop is JObject))
            {
                throw new InvalidOperationException("Unexpected JSON structure in stops.");
            }
        }

        foreach (var route in jObject["routes"] as JArray)
        {
            if (!(route is JObject))
            {
                throw new InvalidOperationException("Unexpected JSON structure in routes.");
            }
        }
    }
}
