[<AutoOpen>]
module Farmer.Builders.CosmosDb

open Farmer
open Farmer.CosmosDb
open Farmer.Arm.DocumentDb
open DatabaseAccounts
open SqlDatabases

type KeyType =
    | PrimaryKey
    | SecondaryKey

    member this.ArmValue =
        match this with
        | PrimaryKey -> "primary"
        | SecondaryKey -> "secondary"

type KeyAccess =
    | ReadWrite
    | ReadOnly

    member this.ArmValue =
        match this with
        | ReadWrite -> ""
        | ReadOnly -> "readonly"

type ConnectionStringKind =
    | PrimaryConnectionString
    | SecondaryConnectionString

    member this.KeyIndex =
        match this with
        | PrimaryConnectionString -> 0
        | SecondaryConnectionString -> 1

type CosmosDb =
    static member private providerPath =
        "providers('Microsoft.DocumentDb','databaseAccounts').apiVersions[0]"

    static member getKey(resourceId: ResourceId, keyType: KeyType, keyAccess: KeyAccess) =
        let expr =
            $"listKeys({resourceId.ArmExpression.Value}, {CosmosDb.providerPath}).{keyType.ArmValue}{keyAccess.ArmValue}MasterKey"

        ArmExpression.create(expr).WithOwner(resourceId)

    static member getKey(name: ResourceName, keyType, keyAccess) =
        CosmosDb.getKey (databaseAccounts.resourceId name, keyType, keyAccess)

    static member getConnectionString(resourceId: ResourceId, connectionStringKind: ConnectionStringKind) =
        let expr =
            $"listConnectionStrings({resourceId.ArmExpression.Value}, {CosmosDb.providerPath}).connectionStrings[{connectionStringKind.KeyIndex}].connectionString"

        ArmExpression.create(expr).WithOwner(resourceId)

    static member getConnectionString(name: ResourceName, connectionStringKind) =
        CosmosDb.getConnectionString (databaseAccounts.resourceId name, connectionStringKind)

type CosmosDbContainerConfig =
    {
        Name: ResourceName
        PartitionKey: string list * IndexKind
        Indexes: (string * (IndexDataType * IndexKind) list) list
        UniqueKeys: Set<string list>
        ExcludedPaths: string list
        ContainerThroughput: Throughput option
    }

type CosmosDbConfig =
    {
        AccountName: ResourceRef<CosmosDbConfig>
        AccountConsistencyPolicy: ConsistencyPolicy
        AccountFailoverPolicy: FailoverPolicy
        DbName: ResourceName
        DbThroughput: Throughput option
        Containers: CosmosDbContainerConfig list
        PublicNetworkAccess: FeatureFlag
        RestrictToAzureServices: FeatureFlag
        FreeTier: bool
        Tags: Map<string, string>
        BackupPolicy: BackupPolicy
        Kind: DatabaseKind
    }

    member private this.AccountResourceId = this.AccountName.resourceId this

    member this.PrimaryKey =
        CosmosDb.getKey (this.AccountResourceId, PrimaryKey, ReadWrite)

    member this.SecondaryKey =
        CosmosDb.getKey (this.AccountResourceId, SecondaryKey, ReadWrite)

    member this.PrimaryReadonlyKey =
        CosmosDb.getKey (this.AccountResourceId, PrimaryKey, ReadOnly)

    member this.SecondaryReadonlyKey =
        CosmosDb.getKey (this.AccountResourceId, SecondaryKey, ReadOnly)

    member this.PrimaryConnectionString =
        CosmosDb.getConnectionString (this.AccountResourceId, PrimaryConnectionString)

    member this.SecondaryConnectionString =
        CosmosDb.getConnectionString (this.AccountResourceId, SecondaryConnectionString)

    member this.Endpoint =
        ArmExpression
            .reference(databaseAccounts, this.AccountResourceId)
            .Map(sprintf "%s.documentEndpoint")

    interface IBuilder with
        member this.ResourceId = this.AccountResourceId

        member this.BuildResources location =
            [
                // Account
                match this.AccountName with
                | DeployableResource this _ ->
                    {
                        Name = this.AccountResourceId.Name
                        Location = location
                        Kind = this.Kind
                        ConsistencyPolicy = this.AccountConsistencyPolicy
                        Serverless =
                            match this.DbThroughput with
                            | Some Serverless -> Enabled
                            | _ -> Disabled
                        PublicNetworkAccess = this.PublicNetworkAccess
                        RestrictToAzureServices = this.RestrictToAzureServices
                        FailoverPolicy = this.AccountFailoverPolicy
                        FreeTier = this.FreeTier
                        BackupPolicy = this.BackupPolicy
                        Tags = this.Tags
                    }
                | _ -> ()

                // Database
                {
                    Name = this.DbName
                    Account = this.AccountResourceId.Name
                    Throughput = this.DbThroughput
                    Kind = this.Kind
                }

                // Containers
                for container in this.Containers do
                    {
                        Name = container.Name
                        Account = this.AccountResourceId.Name
                        Database = this.DbName
                        PartitionKey =
                            {|
                                Paths = fst container.PartitionKey
                                Kind = snd container.PartitionKey
                            |}
                        UniqueKeyPolicy =
                            {|
                                UniqueKeys =
                                    container.UniqueKeys
                                    |> Set.map (fun uniqueKeyPath -> {| Paths = uniqueKeyPath |})
                            |}
                        IndexingPolicy =
                            {|
                                ExcludedPaths = container.ExcludedPaths
                                IncludedPaths =
                                    [
                                        for (path, indexes) in container.Indexes do
                                            {| Path = path; Indexes = indexes |}
                                    ]
                            |}
                        Throughput = container.ContainerThroughput
                    }
            ]

type CosmosDbContainerBuilder() =
    member _.Yield _ =
        {
            Name = ResourceName ""
            PartitionKey = [], Hash
            Indexes = []
            UniqueKeys = Set.empty
            ExcludedPaths = []
            ContainerThroughput = None
        }

    member _.Run state =
        match state.ContainerThroughput with
        | Some Serverless -> raiseFarmer "Container throughput can must be one of 'Provisioned' or 'Autoscale'"
        | _ -> ()

        match state.PartitionKey with
        | [], _ -> raiseFarmer $"You must set a partition key on CosmosDB container '{state.Name.Value}'."
        | partitions, indexKind ->
            { state with
                PartitionKey =
                    [
                        for partition in partitions do
                            if partition.StartsWith "/" then
                                partition
                            else
                                "/" + partition
                    ],
                    indexKind
            }

    /// Sets the name of the container.
    [<CustomOperation "name">]
    member _.Name(state: CosmosDbContainerConfig, name) = { state with Name = ResourceName name }

    /// Sets the partition key of the container.
    [<CustomOperation "partition_key">]
    member _.PartitionKey(state: CosmosDbContainerConfig, partitions, indexKind) =
        { state with
            PartitionKey = partitions, indexKind
        }

    /// Adds an index to the container.
    [<CustomOperation "add_index">]
    member _.AddIndex(state: CosmosDbContainerConfig, path, indexes) =
        { state with
            Indexes = (path, indexes) :: state.Indexes
        }

    /// Adds multiple indexes to the container.
    [<CustomOperation "add_indexes">]
    member _.AddIndexes(state: CosmosDbContainerConfig, indexes: (string * (IndexDataType * IndexKind) list) list) =
        { state with
            Indexes = List.append indexes state.Indexes
        }

    /// Adds a unique key constraint to the container (ensures uniqueness within the logical partition).
    [<CustomOperation "add_unique_key">]
    member _.AddUniqueKey(state: CosmosDbContainerConfig, uniqueKeyPaths) =
        { state with
            UniqueKeys = state.UniqueKeys.Add(uniqueKeyPaths)
        }

    /// Excludes a path from the container index.
    [<CustomOperation "exclude_path">]
    member _.ExcludePath(state: CosmosDbContainerConfig, path) =
        { state with
            ExcludedPaths = path :: state.ExcludedPaths
        }

    /// Sets the throughput of the container.
    [<CustomOperation "throughput">]
    member _.Throughput(state: CosmosDbContainerConfig, throughput) =
        { state with
            ContainerThroughput = Some throughput
        }

    member _.Throughput(state: CosmosDbContainerConfig, throughput) =
        { state with
            ContainerThroughput = Some(Provisioned throughput)
        }

    member _.Throughput(state: CosmosDbContainerConfig, throughput) =
        { state with
            ContainerThroughput = throughput
        }

type CosmosDbBuilder() =
    member _.Yield _ =
        {
            DbName = ResourceName.Empty
            AccountName =
                derived (fun config ->
                    let dbName = config.DbName.Value.ToLower()
                    let maxLength = 36 // 44 less "-account"

                    if config.DbName.Value.Length > maxLength then
                        dbName.Substring maxLength
                    else
                        dbName
                    |> sprintf "%s-account"
                    |> ResourceName
                    |> databaseAccounts.resourceId)
            AccountConsistencyPolicy = Eventual
            AccountFailoverPolicy = NoFailover
            DbThroughput = Some(Provisioned 400<RU>)
            Containers = []
            PublicNetworkAccess = Enabled
            RestrictToAzureServices = Disabled
            FreeTier = false
            Tags = Map.empty
            BackupPolicy = BackupPolicy.NoBackup
            Kind = DatabaseKind.Document
        }

    member _.Run state =
        let containersWithoutThroughput =
            state.Containers |> List.filter (fun c -> c.ContainerThroughput.IsNone)

        match state.DbThroughput, containersWithoutThroughput with
        | None, [ _ ] ->
            raiseFarmer
                "One or more containers have no throughput specified. Either set database (shared) throughput, or set dedicated throughput against each container."
        | _ -> ()

        state

    /// Sets the name of the CosmosDB server.
    [<CustomOperation "account_name">]
    member _.AccountName(state: CosmosDbConfig, accountName: ResourceName) =
        { state with
            AccountName =
                AutoGeneratedResource(
                    Named(
                        databaseAccounts.resourceId (
                            CosmosDbValidation.CosmosDbName.Create(accountName).OkValue.ResourceName
                        )
                    )
                )
        }

    member this.AccountName(state: CosmosDbConfig, accountName: string) =
        this.AccountName(state, ResourceName accountName)

    /// Links the database to an existing server
    [<CustomOperation "link_to_account">]
    member _.LinkToAccount(state: CosmosDbConfig, accountConfig: CosmosDbConfig) =
        { state with
            AccountName = LinkedResource(Managed(accountConfig.AccountName.resourceId accountConfig))
        }

    /// Sets the name of the database.
    [<CustomOperation "name">]
    member _.Name(state: CosmosDbConfig, name) = { state with DbName = name }

    member this.Name(state: CosmosDbConfig, name: string) = this.Name(state, ResourceName name)

    /// Sets the consistency policy of the database.
    [<CustomOperation "consistency_policy">]
    member _.ConsistencyPolicy(state: CosmosDbConfig, consistency: ConsistencyPolicy) =
        { state with
            AccountConsistencyPolicy = consistency
        }

    /// Sets the failover policy of the database.
    [<CustomOperation "failover_policy">]
    member _.FailoverPolicy(state: CosmosDbConfig, failoverPolicy: FailoverPolicy) =
        { state with
            AccountFailoverPolicy = failoverPolicy
        }

    /// Sets the throughput of the server.
    [<CustomOperation "throughput">]
    member _.Throughput(state: CosmosDbConfig, throughput) =
        { state with
            DbThroughput = Some throughput
        }

    member _.Throughput(state: CosmosDbConfig, throughput) =
        { state with
            DbThroughput = Some(Provisioned throughput)
        }

    member _.Throughput(state: CosmosDbConfig, throughput) =
        { state with DbThroughput = throughput }

    /// Sets the storage kind
    [<CustomOperation "kind">]
    member _.StorageKind(state: CosmosDbConfig, kind) = { state with Kind = kind }

    /// Adds a list of containers to the database.
    [<CustomOperation "add_containers">]
    member _.AddContainers(state: CosmosDbConfig, containers) =
        { state with
            Containers = state.Containers @ containers
        }

    /// Enables public network access
    [<CustomOperation "enable_public_network_access">]
    member _.PublicNetworkAccess(state: CosmosDbConfig) =
        { state with
            PublicNetworkAccess = Enabled
        }

    /// Disables public network access
    [<CustomOperation "disable_public_network_access">]
    member _.PrivateNetworkAccess(state: CosmosDbConfig) =
        { state with
            PublicNetworkAccess = Disabled
        }

    /// Enables the use of CosmosDB free tier (one per subscription).
    [<CustomOperation "free_tier">]
    member _.FreeTier(state: CosmosDbConfig) = { state with FreeTier = true }

    /// Sets the backup policy of the database
    [<CustomOperation "backup_policy">]
    member _.BackupPolicy(state: CosmosDbConfig, backupPolicy: BackupPolicy) =
        { state with
            BackupPolicy = backupPolicy
        }

    /// Add an IP rule which only allows access from Azure services.
    [<CustomOperation "restrict_to_azure_services">]
    member _.RestrictToAzureServices(state: CosmosDbConfig) =
        { state with
            RestrictToAzureServices = Enabled
        }

    interface ITaggable<CosmosDbConfig> with
        member _.Add state tags =
            { state with
                Tags = state.Tags |> Map.merge tags
            }

let cosmosDb = CosmosDbBuilder()
let cosmosContainer = CosmosDbContainerBuilder()
