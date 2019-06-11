// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.TableStorage.Configs;
using Microsoft.Health.Fhir.ValueSets;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

namespace Microsoft.Health.Fhir.TableStorage.Features.Storage
{
    public class TableStorageFhirDataStore : IFhirDataStore, IProvideCapability
    {
        private readonly CloudTable _table;

        public TableStorageFhirDataStore(CloudTableClient client, TableStorageDataStoreConfiguration config)
        {
            _table = client.GetTableReference(config.TableName);
            _table.CreateIfNotExistsAsync().GetAwaiter().GetResult();
        }

        public async Task<UpsertOutcome> UpsertAsync(
            ResourceWrapper resource,
            WeakETag weakETag,
            bool allowCreate,
            bool keepHistory,
            CancellationToken cancellationToken)
        {
            var generator = new SearchIndexEntryGenerator();
            Dictionary<string, EntityProperty> entries = resource.SearchIndices.SelectMany(x => generator.Generate(x)).ToDictionary(x => x.Key, x => x.Value);

            FhirTableEntity entity;

            using (var stream = new MemoryStream())
            using (var gzipStream = new GZipStream(stream, CompressionMode.Compress))
            using (var writer = new StreamWriter(gzipStream, Encoding.UTF8))
            {
                writer.Write(resource.RawResource.Data);
                writer.Flush();

                entity = new FhirTableEntity(
                    resource.ResourceId,
                    resource.Version ?? Guid.NewGuid().ToString(),
                    resource.ResourceTypeName,
                    stream.ToArray(),
                    resource.Request.Method,
                    resource.Request.Url.ToString(),
                    resource.LastModified,
                    resource.IsDeleted,
                    resource.IsHistory,
                    entries);
            }

            TableResult tableResult;
            var create = true;

            try
            {
                // Optimize for insert
                tableResult = await _table.ExecuteAsync(TableOperation.Insert(entity));
            }
            catch (StorageException ex) when (ex.RequestInformation.HttpStatusCode == 409)
            {
                TableOperation retrieveOperation = TableOperation.Retrieve<FhirTableEntity>(resource.ResourceTypeName, FhirTableEntity.CreateId(resource.ResourceId));
                TableResult existingResult = await _table.ExecuteAsync(retrieveOperation);
                var existing = (FhirTableEntity)existingResult.Result;

                existing.IsHistory = true;
                existing.PartitionKey = $"{existing.ResourceTypeName}_History";
                existing.RowKey = FhirTableEntity.CreateId(existing.ResourceId, existing.VersionId);
                create = false;

                // Optimistic concurrency check
                entity.ETag = existing.ETag;
                tableResult = await _table.ExecuteAsync(TableOperation.Replace(entity));
                await _table.ExecuteAsync(TableOperation.Insert(existing));
            }

            return new UpsertOutcome(ToResourceWrapper((FhirTableEntity)tableResult.Result), create ? SaveOutcomeType.Created : SaveOutcomeType.Updated);
        }

        public async Task<ResourceWrapper> GetAsync(ResourceKey key, CancellationToken cancellationToken)
        {
            TableOperation retrieveOperation;
            FhirTableEntity result = null;

            if (!string.IsNullOrEmpty(key.VersionId))
            {
                retrieveOperation = TableOperation.Retrieve<FhirTableEntity>($"{key.ResourceType}_History", FhirTableEntity.CreateId(key.Id, key.VersionId));
                result = (FhirTableEntity)(await _table.ExecuteAsync(retrieveOperation)).Result;
            }

            if (result == null)
            {
                retrieveOperation = TableOperation.Retrieve<FhirTableEntity>(key.ResourceType, FhirTableEntity.CreateId(key.Id));
                result = (FhirTableEntity)(await _table.ExecuteAsync(retrieveOperation)).Result;

                if (!string.IsNullOrEmpty(key.VersionId) && result.VersionId != key.VersionId)
                {
                    return null;
                }
            }

            return ToResourceWrapper(result);
        }

        internal static ResourceWrapper ToResourceWrapper(FhirTableEntity result)
        {
            string rawResource;

            using (var stream = new MemoryStream(result.RawResourceData))
            using (var gzipStream = new GZipStream(stream, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzipStream, Encoding.UTF8))
            {
                rawResource = reader.ReadToEnd();
            }

            return new ResourceWrapper(
                result.ResourceId,
                result.VersionId,
                result.ResourceTypeName,
                new RawResource(rawResource, FhirResourceFormat.Json),
                new ResourceRequest(result.ResourceRequestMethod, new Uri(result.ResourceRequestUri)),
                result.LastModified,
                result.IsDeleted,
                new List<SearchIndexEntry>(),
                new CompartmentIndices(),
                new List<KeyValuePair<string, string>>())
            {
                IsHistory = result.IsHistory,
            };
        }

        public Task HardDeleteAsync(ResourceKey key, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public void Build(IListedCapabilityStatement statement)
        {
            EnsureArg.IsNotNull(statement, nameof(statement));

            foreach (var resource in ModelInfoProvider.GetResourceTypeNames())
            {
                statement.BuildRestResourceComponent(resource, builder =>
                {
                    builder.AddResourceVersionPolicy(ResourceVersionPolicy.NoVersion);
                    builder.AddResourceVersionPolicy(ResourceVersionPolicy.Versioned);
                    builder.AddResourceVersionPolicy(ResourceVersionPolicy.VersionedUpdate);
                    builder.ReadHistory = true;
                    builder.UpdateCreate = true;
                });
            }
        }
    }
}
