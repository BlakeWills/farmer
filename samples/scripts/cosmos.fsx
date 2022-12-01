#r "nuget:Farmer"

open Farmer
open Farmer.Builders
open Farmer.CosmosDb

let myCosmosDb = cosmosDb {
    name "isaacsappdb"
    account_name "isaacscosmosdb"
    throughput (CosmosDb.Throughput.Autoscale(1000<CosmosDb.RU>)) // Shared throughput
    failover_policy NoFailover
    consistency_policy (BoundedStaleness(500, 1000))
    add_containers [
        cosmosContainer {
            name "myContainer"
            throughput 400<CosmosDb.RU> // Dedicated container throughput
            partition_key [ "/id" ] Hash
            add_index "/path/?" [ Number, Hash ]
            add_indexes [
                ("/pathone/?", [ String, Range ])
                ("/pathtwo/?", [ String, Range ])
            ]
            exclude_path "/*"
        }
        cosmosContainer {
            name "myOtherContainer"
            partition_key [ "/id" ] Hash
            add_index "/path/?" [ Number, Hash ]
            exclude_path "/*"
        }
    ]
    restrict_to_azure_services
    backup_policy (CosmosDb.BackupPolicy.Periodic(
        BackupIntervalInMinutes = 60,
        BackupRetentionIntervalInHours = 168,
        BackupStorageRedundancy = CosmosDb.BackupStorageRedundancy.Geo))
}

let deployment =
    arm {
        location Location.NorthEurope
        add_resource myCosmosDb
    }

deployment
|> Deploy.execute "my-resource-group-name" Deploy.NoParameters
