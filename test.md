# Perform the tests
## device simulator
this: https://docs.microsoft.com/en-us/samples/azure-samples/iot-telemetry-simulator/azure-iot-device-telemetry-simulator/

fire 5000 events with a rate = 1 event/s
```bash
docker run -it \
    -e IotHubConnectionString="HostName=adt-tests-1.azure-devices.net;SharedAccessKeyName=device;SharedAccessKey=jjF4s9zBHluY5+ebYKPuLyrERp1q+OaeFaeL9TGeyes=" \
    -e DeviceList="device" \
    -e Template="{ \"deviceId\": \"$.DeviceId\", \"Temperature\": $.Temp, \"Humidity\": $.Hum, \"Counter\": $.Counter, \"time\": \"$.Time\"}" \
    -e Variables="[{name: \"Temp\", \"random\": true, \"max\": 25, \"min\": 23}, {name: \"Hum\", \"random\": true, \"max\": 0, \"min\": 100}, {\"name\":\"Counter\", \"min\":100}]" \
    -e Interval="1000" \
    -e MessageCount="5000" \
    mcr.microsoft.com/oss/azure-samples/azureiot-telemetrysimulator
```

## Console app
```bash
dotnet run -- --eh-conn-string="Endpoint=sb://arlotitoadtehns.servicebus.windows.net/;SharedAccessKeyName=EHPolicy;SharedAccessKey=XXXXXXXXXXXXXX;EntityPath=property-event-hub" -n "property-event-hub"

dotnet run -- --eh-conn-string="Endpoint=sb://arlotitoadtehns.servicebus.windows.net/;SharedAccessKeyName=EHPolicy;SharedAccessKey=YYYYYYYYYYYYYY;EntityPath=telemetry-event-hub" -n "telemetry-event-hub"
```