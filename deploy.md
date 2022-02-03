# Deploy the solution
## setup ADT instance and authentication
```bash
USER=<your-email> #example: me@contoso.com

# create RG
az group create --location westeurope --resource-group adt-tests-rg

# create ADT instance
az dt create --dt-name adt-tests-1 --resource-group adt-tests-rg --location westeurope

# get properties
az dt show --dt-name adt-tests-1
ADT_HOSTNAME=$(az dt show --dt-name adt-tests-1 | jq -r .hostName)
ADT_NAME=$(az dt show --dt-name adt-tests-1 | jq -r .name)
ADT_RG=$(az dt show --dt-name adt-tests-1 | jq -r .resourceGroup)

# assign the "ADT Data Owner" role to the user
az dt role-assignment create --dt-name adt-tests-1 --assignee "$USER" --role "Azure Digital Twins Data Owner"

# get name of active subscription
ADT_SUBSCRIPTION=$(az account show | jq .name)
```

# upload model and create digital twin
```bash
# list models (should be none)
az dt model list -n $ADT_NAME

# ---- repeat also just to update model ----
# delete if needed
az dt model delete-all --dt-name $ADT_NAME

# upload model
az dt model create --dt-name $ADT_NAME --models ./model/thermostat.json

# create twin
az dt twin create  --dt-name $ADT_NAME --dtmi "dtmi:contosocom:DigitalTwins:Thermostat;1" --twin-id thermostat67
# ---- repeat also just to update model ----
```

## create function, assign role and set env variables
(from here: https://aka.ms/ADT-HOL and here: https://docs.microsoft.com/en-us/azure/digital-twins/how-to-ingest-iot-hub-data?tabs=cli)

```bash
# create storage account
az storage account create --name arlotitoadtstorage --location westeurope --resource-group $ADT_RG --sku Standard_LRS

# create azure function (runtime v4, GA and recommended, dotnet 6)
telemetryfunctionname=arlotito-adt-hubtotwin
az functionapp create -g $ADT_RG  -n $telemetryfunctionname -s arlotitoadtstorage --functions-version 4 --consumption-plan-location westeurope

# get function's service principal ID
    # principalID=$(az functionapp identity assign -g $ADT_RG -n $telemetryfunctionname | jq -r .principalId)
principalID=$(az functionapp identity assign -g $ADT_RG -n $telemetryfunctionname --query principalId -o tsv)

# assign ADT Data Owner role to function
az dt role-assignment create --dt-name $ADT_NAME --assignee $principalID --role "Azure Digital Twins Data Owner"

# sets an environment variable for your instance's URL
az functionapp config appsettings set -g $ADT_RG -n $telemetryfunctionname --settings "ADT_SERVICE_URL=https://${ADT_HOSTNAME}"

# sets the target twin id the function writes to
az functionapp config appsettings set -g $ADT_RG -n $telemetryfunctionname --settings "ADT_TWIN_ID=thermostat67"
```

## vscode
* create function (dotnet 6, azure function v4, event grid template, "TwinsFunction", "My.Function")

* add packages
```python
dotnet add package Azure.DigitalTwins.Core
dotnet add package Azure.Identity
```

* code

* publish to function
(if you get strange errors, just remove obj and bin folders)

## create iothub
```bash
az iot hub create --name $ADT_NAME --resource-group $ADT_RG --sku S1 -l westeurope

# create device identity
az iot hub device-identity create --device-id device --hub-name $ADT_NAME -g $ADT_RG
HUB_NAME=$ADT_NAME
DEVICE_CONN_STRING=$(az iot hub device-identity connection-string show -d device --hub-name $HUB_NAME --query connectionString -o tsv)
```

## configure event grid
```bash
# get hub resource id
HUB_RES_ID=$(az iot hub list -g $ADT_RG --query "[?contains(name, '${ADT_NAME}')].id" -o tsv)

# get function name
$function=$(az functionapp function show -n $telemetryfunctionname -g $ADT_RG --function-name TwinsFunction --query id -o tsv)

az eventgrid event-subscription create \
    --name IoTHubEvents --source-resource-id $HUB_RES_ID \
    --endpoint $function --endpoint-type azurefunction \
    --included-event-types Microsoft.Devices.DeviceTelemetry
```



## ADT egress: create endpoints and event routes
```bash

# create EH
ehnamespace="arlotitoadtehns"
az eventhubs namespace create --name $ehnamespace --resource-group $ADT_RG -l "westeurope"

# --------------------------
# create EH for property
az eventhubs eventhub create --name "property-event-hub" --resource-group $ADT_RG --namespace-name $ehnamespace
az eventhubs eventhub authorization-rule create --rights Listen Send --resource-group $ADT_RG --namespace-name $ehnamespace --eventhub-name "property-event-hub" --name EHPolicy

# create an event route endpoint
az dt endpoint create eventhub --endpoint-name EHEndpointProperty --eventhub-resource-group $ADT_RG --eventhub-namespace $ehnamespace --eventhub "property-event-hub" --eventhub-policy EHPolicy -n $ADT_NAME

# create the route for property
az dt route create -n $ADT_NAME --endpoint-name EHEndpointProperty --route-name EHRouteProperty --filter "type = 'Microsoft.DigitalTwins.Twin.Update'"

# --------------------------
# create EH for telemetry
az eventhubs eventhub create --name "telemetry-event-hub" --resource-group $ADT_RG --namespace-name $ehnamespace
az eventhubs eventhub authorization-rule create --rights Listen Send --resource-group $ADT_RG --namespace-name $ehnamespace --eventhub-name "telemetry-event-hub" --name EHPolicy

# create an event route endpoint
az dt endpoint create eventhub --endpoint-name EHEndpointTelemetry --eventhub-resource-group $ADT_RG --eventhub-namespace $ehnamespace --eventhub "telemetry-event-hub" --eventhub-policy EHPolicy -n $ADT_NAME

# create the route for telemetry
az dt route create -n $ADT_NAME --endpoint-name EHEndpointTelemetry --route-name EHRouteTelemetry --filter "type = 'Microsoft.DigitalTwins.Telemetry'"
```