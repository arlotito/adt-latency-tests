using System;
using Azure;
using System.Net.Http;
using Azure.Core.Pipeline;
using Azure.DigitalTwins.Core;
using Azure.Identity;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

namespace IotHubtoTwins
{
    public class IoTHubtoTwins
    {
        private static readonly string adtInstanceUrl = Environment.GetEnvironmentVariable("ADT_SERVICE_URL");
        private static readonly string adtTwinId = Environment.GetEnvironmentVariable("ADT_TWIN_ID");
        private static readonly HttpClient httpClient = new HttpClient();

        [FunctionName("TwinsFunction")]
        public async Task Run([EventGridTrigger] EventGridEvent eventGridEvent, ILogger log)
        {
            if (adtInstanceUrl == null) 
                log.LogError("Application setting \"ADT_SERVICE_URL\" not set");

            try
            {
                if (eventGridEvent != null && eventGridEvent.Data != null)
                {
                    DateTime functionEntrypointTime = DateTime.UtcNow;
                    // Authenticate with Digital Twins
                    var cred = new ManagedIdentityCredential("https://digitaltwins.azure.net");
                    
                    // creates client
                    var client = new DigitalTwinsClient(
                        new Uri(adtInstanceUrl),
                        cred,
                        new DigitalTwinsClientOptions { Transport = new HttpClientTransport(httpClient) });
                    log.LogInformation($"ADT service client connection created.");

                    log.LogInformation(eventGridEvent.Data.ToString());

                    // <Find_device_ID_and_temperature>
                    JObject deviceMessage = (JObject)JsonConvert.DeserializeObject(eventGridEvent.Data.ToString());
                    string deviceId = (string)deviceMessage["systemProperties"]["iothub-connection-device-id"];

                    DateTime iotHubEnqueuedTime = (DateTime)deviceMessage["systemProperties"]["iothub-enqueuedtime"];
                    log.LogInformation($"IotHubEnqueuedTime:    {iotHubEnqueuedTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")}");
                    log.LogInformation($"EventTime:             {eventGridEvent.EventTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")}");


                    var temperature = deviceMessage["body"]["Temperature"];
                    var humidity = deviceMessage["body"]["Humidity"];
                    // </Find_device_ID_and_temperature>

                    log.LogInformation($"Device: {deviceId}, Temperature: {temperature}, Humidity: {humidity}");

                    //from https://docs.microsoft.com/en-us/azure/digital-twins/how-to-manage-twin
                    //- Properties (optional to initialize): You can set initial values for properties of the digital twin if you want. 
                    //  Properties are treated as optional and can be set later, but note that they won't show up as part of a twin until they've been set.
                    //
                    //- Telemetry (recommended to initialize): You can also set initial values for telemetry fields on the twin. 
                    //  Although initializing telemetry isn't required, telemetry fields also won't show up as part of a twin until they've been set. 
                    //  This means that you'll be unable to edit telemetry values for a twin unless they've been initialized first.
                   
                    // updating the property
                    var updateTwinData = new JsonPatchDocument();

                    //note: if you use the AppendReplace, the property must be initialized!
                    updateTwinData.AppendAdd("/TimeA", iotHubEnqueuedTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                    updateTwinData.AppendAdd("/TimeB", eventGridEvent.EventTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                    updateTwinData.AppendAdd("/TimeC", functionEntrypointTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                    DateTime functionPostAuthTime = DateTime.UtcNow;
                    updateTwinData.AppendAdd("/TimeD", functionPostAuthTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                    
                    await client.UpdateDigitalTwinAsync(adtTwinId, updateTwinData);

                    //publishing telemetry
                    //remember: telemetry is not stored in ADT
                    var telemetry = new {
                        TelemetryObject = new {
                            Value = temperature.Value<double>(),
                            EventGridTime = eventGridEvent.EventTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            IngestionFunctionTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                        }
                    };

                    log.LogInformation(JsonConvert.SerializeObject(telemetry));

                    //serializzied object must be escaped (ex. "{\"Telemetry1\": 5}") 
                    //await client.PublishTelemetryAsync(adtTwinId, Guid.NewGuid().ToString(), JsonConvert.SerializeObject(telemetry));

                    await client.PublishTelemetryAsync(adtTwinId, Guid.NewGuid().ToString(), "{\"TelemetryValue\": 5}");
                }
            }
            
            catch (Exception ex)
            {
                log.LogError($"Error in ingest function: {ex.Message}");
            }
        }
    }
}
