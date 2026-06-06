using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine.Scripting;


// ReSharper disable once CheckNamespace

namespace Solana.Unity.SDK
{

    [Serializable]
    [Preserve]
    public class JsonRequest
    {
        [Serializable]
        [Preserve]
        public class JsonRequestIdentity
        {
            [JsonProperty("uri", NullValueHandling = NullValueHandling.Ignore)]
            [RequiredMember]
            public Uri Uri { get; set; }

            [JsonProperty("icon", NullValueHandling = NullValueHandling.Ignore)]
            [RequiredMember]
            public Uri Icon { get; set; }

            [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
            [RequiredMember]
            public string Name { get; set; }

            [Preserve]
            [RequiredMember]
            public JsonRequestIdentity()
            {
            }
        }

        [Serializable]
        [Preserve]
        public class JsonRequestParams
        {
            [JsonProperty("identity", NullValueHandling = NullValueHandling.Ignore)]
            [RequiredMember]
            public JsonRequestIdentity Identity { get; set; }

            [JsonProperty("cluster", NullValueHandling = NullValueHandling.Ignore)]
            [RequiredMember]
            public string Cluster { get; set; }

            // MWA 2.0 chain identifier (e.g. "solana:devnet"). Newer wallets such as
            // Seed Vault on Seeker key the authorization network off this field and default
            // to mainnet when it is absent; older wallets still read the legacy "cluster".
            // We send both for maximum compatibility.
            [JsonProperty("chain", NullValueHandling = NullValueHandling.Ignore)]
            [RequiredMember]
            public string Chain { get; set; }

            [JsonProperty("auth_token", NullValueHandling = NullValueHandling.Ignore)]
            [RequiredMember]
            
            public string AuthToken { get; set; }

            [JsonProperty("payloads", NullValueHandling = NullValueHandling.Ignore)]
            [RequiredMember]
            public List<string> Payloads { get; set; }

            [JsonProperty("addresses", NullValueHandling = NullValueHandling.Ignore)]
            [RequiredMember]
            public List<string> Addresses { get; set; }

            [RequiredMember]
            public JsonRequestParams()
            {
            }
        }

        [JsonProperty("jsonrpc")] 
        [RequiredMember]
        public string JsonRpc { get; set; }

        [JsonProperty("method")] 
        [RequiredMember]
        public string Method { get; set; }

        [JsonProperty("params")] 
        [RequiredMember]
        public JsonRequestParams Params { get; set; }

        [JsonProperty("id")] 
        [RequiredMember]
        public int Id { get; set; }
    }
}