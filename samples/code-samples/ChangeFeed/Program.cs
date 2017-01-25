﻿namespace DocumentDB.Samples.Queries
{
    using Shared.Util;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Client;
    using Microsoft.Azure.Documents.Linq;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Linq;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Converters;

    //------------------------------------------------------------------------------------------------
    // This sample demonstrates how 
    //------------------------------------------------------------------------------------------------

    public class Program
    {
        private static DocumentClient client;

        // Assign an id for your database & collection 
        private static readonly string DatabaseName = ConfigurationManager.AppSettings["DatabaseId"];
        private static readonly string CollectionName = ConfigurationManager.AppSettings["CollectionId"];

        // Read the DocumentDB endpointUrl and authorizationKeys from config
        private static readonly string endpointUrl = ConfigurationManager.AppSettings["EndPointUrl"];
        private static readonly string authorizationKey = ConfigurationManager.AppSettings["AuthorizationKey"];

        public static void Main(string[] args)
        {
            try
            {
                //Get a Document client
                using (client = new DocumentClient(new Uri(endpointUrl), authorizationKey,
                    new ConnectionPolicy { ConnectionMode = ConnectionMode.Direct, ConnectionProtocol = Protocol.Tcp }))
                {
                    RunDemoAsync(DatabaseName, CollectionName).Wait();
                }
            }
#if !DEBUG
            catch (Exception e)
            {
                LogException(e);
            }
#endif
            finally
            {
                Console.WriteLine("End of demo, press any key to exit.");
                Console.ReadKey();
            }
        }

        private static async Task RunDemoAsync(string databaseId, string collectionId)
        {
            Database database = await GetNewDatabaseAsync(databaseId);
            DocumentCollection collection = await GetOrCreateCollectionAsync(databaseId, collectionId);

            Uri collectionUri = UriFactory.CreateDocumentCollectionUri(databaseId, collectionId);

            Console.WriteLine("Inserting 100 documents");
            List<Task> insertTasks = new List<Task>();
            for (int i = 0; i < 100; i++)
            {
                insertTasks.Add(client.CreateDocumentAsync(
                    collectionUri,
                    new DeviceReading { DeviceId = string.Format("xsensr-{0}", i), MetricType = "Temperature", Unit = "Celsius", MetricValue = 990 }));
            }

            await Task.WhenAll(insertTasks);

            // Returns all documents in the collection.
            Console.WriteLine("Reading all changes from the beginning");
            Dictionary<string, string> checkpoints = await GetChanges(client, collectionUri, new Dictionary<string, string>());

            Console.WriteLine("Inserting 2 new documents");
            await client.CreateDocumentAsync(
                collectionUri, 
                new DeviceReading { DeviceId = "xsensr-201", MetricType = "Temperature", Unit = "Celsius", MetricValue = 1000 });
            await client.CreateDocumentAsync(
                collectionUri, 
                new DeviceReading { DeviceId = "xsensr-212", MetricType = "Pressure", Unit = "psi", MetricValue = 1000 });

            // Returns only the two documents created above.
            Console.WriteLine("Reading changes using Change Feed from the last checkpoint");
            checkpoints = await GetChanges(client, collectionUri, checkpoints);
        }

        /// <summary>
        /// Get changes within the collection since the last checkpoint. This sample shows how to process the change 
        /// feed from a single worker. When working with large collections, this is typically split across multiple
        /// workers each processing a single or set of partition key ranges.
        /// </summary>
        /// <param name="client">DocumentDB client instance</param>
        /// <param name="collection">Collection to retrieve changes from</param>
        /// <param name="checkpoints"></param>
        /// <returns></returns>
        private static async Task<Dictionary<string, string>> GetChanges(
            DocumentClient client,
            Uri collectionUri,
            Dictionary<string, string> checkpoints)
        {
            int numChangesRead = 0;
            string pkRangesResponseContinuation = null;
            List<PartitionKeyRange> partitionKeyRanges = new List<PartitionKeyRange>();

            do
            {
                FeedResponse<PartitionKeyRange> pkRangesResponse = await client.ReadPartitionKeyRangeFeedAsync(
                    collectionUri, 
                    new FeedOptions { RequestContinuation = pkRangesResponseContinuation });

                partitionKeyRanges.AddRange(pkRangesResponse);
                pkRangesResponseContinuation = pkRangesResponse.ResponseContinuation;
            }
            while (pkRangesResponseContinuation != null);

            foreach (PartitionKeyRange pkRange in partitionKeyRanges)
            {
                string continuation = null;
                checkpoints.TryGetValue(pkRange.Id, out continuation);

                IDocumentQuery<Document> query = client.CreateDocumentChangeFeedQuery(
                    collectionUri,
                    new ChangeFeedOptions
                    {
                        PartitionKeyRangeId = pkRange.Id,
                        StartFromBeginning = true,
                        RequestContinuation = continuation,
                        MaxItemCount = -1
                    });

                while (query.HasMoreResults)
                {
                    FeedResponse<DeviceReading> readChangesResponse = query.ExecuteNextAsync<DeviceReading>().Result;

                    foreach (DeviceReading changedDocument in readChangesResponse)
                    {
                        Console.WriteLine("\tRead document {0} from the change feed.", changedDocument.Id);
                        numChangesRead++;
                    }

                    checkpoints[pkRange.Id] = readChangesResponse.ResponseContinuation;
                }
            }

            Console.WriteLine("Read {0} documents from the change feed", numChangesRead);

            return checkpoints;
        }

        /// <summary>
        /// Get a Database for this id. Delete if it already exists.
        /// </summary>
        /// <param id="id">The id of the Database to create.</param>
        /// <returns>The created Database object</returns>
        private static async Task<Database> GetNewDatabaseAsync(string id)
        {
            Database database = client.CreateDatabaseQuery().Where(c => c.Id == id).ToArray().FirstOrDefault();
            if (database != null)
            {
                await client.DeleteDatabaseAsync(database.SelfLink);
            }

            database = await client.CreateDatabaseAsync(new Database { Id = id });
            return database;
        }

        /// <summary>
        /// Get a DocuemntCollection by id, or create a new one if one with the id provided doesn't exist.
        /// </summary>
        /// <param name="id">The id of the DocumentCollection to search for, or create.</param>
        /// <returns>The matched, or created, DocumentCollection object</returns>
        private static async Task<DocumentCollection> GetOrCreateCollectionAsync(string databaseId, string collectionId)
        {
            DocumentCollection collection = client.CreateDocumentCollectionQuery(UriFactory.CreateDatabaseUri(databaseId))
                .Where(c => c.Id == collectionId)
                .ToArray()
                .SingleOrDefault();

            if (collection == null)
            {
                DocumentCollection collectionDefinition = new DocumentCollection();
                collectionDefinition.Id = collectionId;
                collectionDefinition.IndexingPolicy = new IndexingPolicy(new RangeIndex(DataType.String) { Precision = -1 });
                collectionDefinition.PartitionKey.Paths.Add("/deviceId");

                collection = await DocumentClientHelper.CreateDocumentCollectionWithRetriesAsync(
                    client,
                    databaseId,
                    collectionDefinition,
                    10100);
            }

            return collection;
        }

        /// <summary>
        /// Log exception error message to the console
        /// </summary>
        /// <param name="e">The caught exception.</param>
        private static void LogException(Exception e)
        {
            ConsoleColor color = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;

            Exception baseException = e.GetBaseException();
            if (e is DocumentClientException)
            {
                DocumentClientException de = (DocumentClientException)e;
                Console.WriteLine("{0} error occurred: {1}, Message: {2}", de.StatusCode, de.Message, baseException.Message);
            }
            else
            {
                Console.WriteLine("Error: {0}, Message: {1}", e.Message, baseException.Message);
            }

            Console.ForegroundColor = color;
        }

        public class DeviceReading
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("deviceId")]
            public string DeviceId { get; set; }

            [JsonConverter(typeof(IsoDateTimeConverter))]
            [JsonProperty("readingTime")]
            public DateTime ReadingTime { get; set; }

            [JsonProperty("metricType")]
            public string MetricType { get; set; }

            [JsonProperty("unit")]
            public string Unit { get; set; }

            [JsonProperty("metricValue")]
            public double MetricValue { get; set; }
        }
    }
}
