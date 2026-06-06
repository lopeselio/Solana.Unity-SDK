using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Solana.Unity.Rpc.Models;
using Solana.Unity.Wallet;
using UnityEngine;
using WebSocketSharp;

// ReSharper disable once CheckNamespace

namespace Solana.Unity.SDK
{
    
    [Serializable]
    public class SolanaMobileWalletAdapterOptions
    {
        public string identityUri = "https://solana.unity-sdk.gg/";
        public string iconUri = "/favicon.ico";
        public string name = "Solana.Unity-SDK";
        public bool keepConnectionAlive = true;
    }
    
    
    [Obsolete("Use SolanaWalletAdapter class instead, which is the cross platform wrapper.")]
    public class SolanaMobileWalletAdapter : WalletBase
    {
        private const string PrefKeyPublicKey = "solana_sdk.mwa.public_key";
        private const string PrefKeyAuthToken = "solana_sdk.mwa.auth_token";
        // Records the MWA chain the cached auth token was authorized against. A token minted by an
        // older build (or against a different network) is not re-scoped by reauthorize, so we compare
        // this against the current chain and force a fresh authorize on mismatch. This is what fixes
        // the intermittent "network mismatch" on Seed Vault/Seeker where a stale token stayed mainnet.
        private const string PrefKeyChain = "solana_sdk.mwa.chain";
        
        private readonly SolanaMobileWalletAdapterOptions _walletOptions;
        
        private Transaction _currentTransaction;

        private TaskCompletionSource<Account> _loginTaskCompletionSource;
        private TaskCompletionSource<Transaction> _signedTransactionTaskCompletionSource;
        private readonly WalletBase _internalWallet;
        private string _authToken;

        public event Action OnWalletDisconnected;
        public event Action OnWalletReconnected;

        public SolanaMobileWalletAdapter(
            SolanaMobileWalletAdapterOptions solanaWalletOptions,
            RpcCluster rpcCluster = RpcCluster.DevNet, 
            string customRpcUri = null, 
            string customStreamingRpcUri = null, 
            bool autoConnectOnStartup = false) : base(rpcCluster, customRpcUri, customStreamingRpcUri, autoConnectOnStartup
        )
        {
            _walletOptions = solanaWalletOptions;
            if (Application.platform != RuntimePlatform.Android)
            {
                throw new Exception("SolanaMobileWalletAdapter can only be used on Android");
            }
            MigrateLegacyPrefKeys();
        }

        private static void MigrateLegacyPrefKeys()
        {
            const string legacyPk = "pk";
            const string legacyAuthToken = "authToken";

            if (!PlayerPrefs.HasKey(legacyPk) && !PlayerPrefs.HasKey(legacyAuthToken))
                return;

            if (PlayerPrefs.HasKey(legacyPk) && !PlayerPrefs.HasKey(PrefKeyPublicKey))
                PlayerPrefs.SetString(PrefKeyPublicKey, PlayerPrefs.GetString(legacyPk));

            if (PlayerPrefs.HasKey(legacyAuthToken) && !PlayerPrefs.HasKey(PrefKeyAuthToken))
                PlayerPrefs.SetString(PrefKeyAuthToken, PlayerPrefs.GetString(legacyAuthToken));

            PlayerPrefs.DeleteKey(legacyPk);
            PlayerPrefs.DeleteKey(legacyAuthToken);
            PlayerPrefs.Save();
        }

        protected override async Task<Account> _Login(string password = null)
        {
            var chain = ChainNameMap[(int)RpcCluster];
            if (_walletOptions.keepConnectionAlive)
            {
                string pk = PlayerPrefs.GetString(PrefKeyPublicKey, null);
                string authToken = PlayerPrefs.GetString(PrefKeyAuthToken, null);
                string cachedChain = PlayerPrefs.GetString(PrefKeyChain, null);
                // Only reuse the cached token if it was authorized against the current chain.
                // A token from an older build has no recorded chain (null) and may be mainnet-scoped,
                // so we drop it and force a fresh authorize below rather than risk a network mismatch.
                bool chainMatches = string.Equals(cachedChain, chain, StringComparison.Ordinal);
                if (!pk.IsNullOrEmpty() && !authToken.IsNullOrEmpty() && chainMatches)
                {
                    string reauthPublicKey = null;
                    // TODO: change to using var after PR #260 merges (IDisposable not yet on LocalAssociationScenario)
                    var reauthorizeScenario = new LocalAssociationScenario();
                    var reauthorizeResult = await reauthorizeScenario.StartAndExecute(
                        new List<Action<IAdapterOperations>>
                        {
                            async client =>
                            {
                                var reauth = await client.Reauthorize(
                                    new Uri(_walletOptions.identityUri),
                                    new Uri(_walletOptions.iconUri, UriKind.Relative),
                                    _walletOptions.name, authToken, chain);
                                if (reauth != null && !string.IsNullOrEmpty(reauth.AuthToken))
                                {
                                    _authToken = reauth.AuthToken;
                                    reauthPublicKey = reauth.PublicKey != null
                                        ? new PublicKey(reauth.PublicKey).ToString()
                                        : null;
                                }
                            }
                        }
                    );
                    if (reauthorizeResult.WasSuccessful)
                    {
                        if (string.IsNullOrEmpty(_authToken))
                        {
                            // Reauthorize RPC succeeded but wallet returned no token - treat as failure
                            // Fall through to cleanup below
                        }
                        else
                        {
                            PlayerPrefs.SetString(PrefKeyAuthToken, _authToken);
                            PlayerPrefs.Save();
                            var resolvedKey = !string.IsNullOrEmpty(reauthPublicKey) ? reauthPublicKey : pk;
                            return new Account(string.Empty, new PublicKey(resolvedKey));
                        }
                    }
                    // Reauthorize failed or returned empty token - clear cached credentials
                    PlayerPrefs.DeleteKey(PrefKeyPublicKey);
                    PlayerPrefs.DeleteKey(PrefKeyAuthToken);
                    PlayerPrefs.DeleteKey(PrefKeyChain);
                    PlayerPrefs.Save();
                }
                else if (!pk.IsNullOrEmpty() || !authToken.IsNullOrEmpty())
                {
                    // Leftover credentials we won't reuse (e.g. chain mismatch or a stale token from
                    // an older build) - drop them so the fresh authorize below binds the correct chain.
                    PlayerPrefs.DeleteKey(PrefKeyPublicKey);
                    PlayerPrefs.DeleteKey(PrefKeyAuthToken);
                    PlayerPrefs.DeleteKey(PrefKeyChain);
                    PlayerPrefs.Save();
                }
            }
            AuthorizationResult authorization = null;
            var localAssociationScenario = new LocalAssociationScenario();
            var cluster = RPCNameMap[(int)RpcCluster];
            var result = await localAssociationScenario.StartAndExecute(
                new List<Action<IAdapterOperations>>
                {
                    async client =>
                    {
                        authorization = await client.Authorize(
                            new Uri(_walletOptions.identityUri),
                            new Uri(_walletOptions.iconUri, UriKind.Relative),
                            _walletOptions.name, cluster, chain);
                    }
                }
            );
            if (!result.WasSuccessful)
            {
                Debug.LogError(result.Error.Message);
                throw new Exception(result.Error.Message);
            }
            if (authorization == null)
            {
                throw new Exception("[MWA] Login: authorization was not populated");
            }
            var publicKey = new PublicKey(authorization.PublicKey);
            if (!string.IsNullOrEmpty(authorization.AuthToken))
            {
                _authToken = authorization.AuthToken;
                if (_walletOptions.keepConnectionAlive)
                {
                    PlayerPrefs.SetString(PrefKeyPublicKey, publicKey.ToString());
                    PlayerPrefs.SetString(PrefKeyAuthToken, _authToken);
                    PersistChain(chain);
                    PlayerPrefs.Save();
                }
            }
            return new Account(string.Empty, publicKey);
        }

        // Persists the chain the cached auth token is scoped to (or clears it for localnet/null),
        // so a later login can detect a mismatch and re-authorize instead of reusing a stale token.
        private static void PersistChain(string chain)
        {
            if (string.IsNullOrEmpty(chain))
                PlayerPrefs.DeleteKey(PrefKeyChain);
            else
                PlayerPrefs.SetString(PrefKeyChain, chain);
        }

        protected override async Task<Transaction> _SignTransaction(Transaction transaction)
        {
            var result = await _SignAllTransactions(new Transaction[] { transaction });
            return result[0];
        }


        protected override async Task<Transaction[]> _SignAllTransactions(Transaction[] transactions)
        {
            if (_authToken.IsNullOrEmpty() && _walletOptions.keepConnectionAlive)
                _authToken = PlayerPrefs.GetString(PrefKeyAuthToken, null);

            var cluster = RPCNameMap[(int)RpcCluster];
            var chain = ChainNameMap[(int)RpcCluster];
            SignedResult res = null;
            var localAssociationScenario = new LocalAssociationScenario();
            AuthorizationResult authorization = null;
            var result = await localAssociationScenario.StartAndExecute(
                new List<Action<IAdapterOperations>>
                {
                    async client =>
                    {
                        if (_authToken.IsNullOrEmpty())
                        {
                            authorization = await client.Authorize(
                                new Uri(_walletOptions.identityUri),
                                new Uri(_walletOptions.iconUri, UriKind.Relative),
                                _walletOptions.name, cluster, chain);
                        }
                        else
                        {
                            authorization = await client.Reauthorize(
                                new Uri(_walletOptions.identityUri),
                                new Uri(_walletOptions.iconUri, UriKind.Relative),
                                _walletOptions.name, _authToken, chain);
                        }
                    },
                    async client =>
                    {
                        res = await client.SignTransactions(transactions.Select(transaction => transaction.Serialize()).ToList());
                    }
                }
            );
            if (!result.WasSuccessful)
            {
                Debug.LogError(result.Error.Message);
                throw new Exception(result.Error.Message);
            }
            if (authorization == null)
            {
                throw new Exception("[MWA] SignAllTransactions: authorization was not populated");
            }
            if (res == null)
            {
                throw new Exception("[MWA] SignAllTransactions: signed payloads were not populated");
            }
            if (!string.IsNullOrEmpty(authorization.AuthToken))
            {
                _authToken = authorization.AuthToken;
                if (_walletOptions.keepConnectionAlive)
                {
                    PlayerPrefs.SetString(PrefKeyAuthToken, _authToken);
                    PersistChain(chain);
                    PlayerPrefs.Save();
                }
            }
            return res.SignedPayloads.Select(transaction => Transaction.Deserialize(transaction)).ToArray();
        }


        public override void Logout()
        {
            base.Logout();
            PlayerPrefs.DeleteKey(PrefKeyPublicKey);
            _authToken = null;
            PlayerPrefs.DeleteKey(PrefKeyAuthToken);
            PlayerPrefs.DeleteKey(PrefKeyChain);
            PlayerPrefs.Save();
        }

        public async Task DisconnectWallet()
        {
            string authToken = _authToken;
            if (authToken.IsNullOrEmpty())
                authToken = PlayerPrefs.GetString(PrefKeyAuthToken, null);

            if (!authToken.IsNullOrEmpty())
            {
                try
                {
                    // TODO: change to using var after PR #260 merges (IDisposable not yet on LocalAssociationScenario)
                    var localAssociationScenario = new LocalAssociationScenario();
                    var result = await localAssociationScenario.StartAndExecute(
                        new List<Action<IAdapterOperations>>
                        {
                            async client =>
                            {
                                await client.Deauthorize(authToken);
                            }
                        }
                    );
                    if (!result.WasSuccessful)
                    {
                        Debug.LogWarning($"[MWA] Deauthorize returned error: {result.Error.Message}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MWA] Deauthorize transport failed (best-effort): {e}");
                }
            }

            Logout();
            OnWalletDisconnected?.Invoke();
        }

        public async Task ReconnectWallet()
        {
            try
            {
                var account = await Login();
                if (account != null)
                {
                    OnWalletReconnected?.Invoke();
                }
                else
                {
                    Debug.LogWarning("[MWA] ReconnectWallet: Login returned null, not firing OnWalletReconnected");
                    throw new Exception("ReconnectWallet failed: Login returned null");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[MWA] ReconnectWallet failed: {e}");
                throw;
            }
        }

        public async Task<CapabilitiesResult> GetCapabilities()
        {
            CapabilitiesResult capabilities = null;
            // TODO: change to using var after PR #260 merges (IDisposable not yet on LocalAssociationScenario)
            var localAssociationScenario = new LocalAssociationScenario();
            var result = await localAssociationScenario.StartAndExecute(
                new List<Action<IAdapterOperations>>
                {
                    async client =>
                    {
                        capabilities = await client.GetCapabilities();
                    }
                }
            );
            if (!result.WasSuccessful)
            {
                Debug.LogError(result.Error.Message);
                throw new Exception(result.Error.Message);
            }
            if (capabilities == null)
            {
                throw new Exception("[MWA] GetCapabilities RPC succeeded but returned no data");
            }
            return capabilities;
        }

        public override async Task<byte[]> SignMessage(byte[] message)
        {
            if (_authToken.IsNullOrEmpty() && _walletOptions.keepConnectionAlive)
                _authToken = PlayerPrefs.GetString(PrefKeyAuthToken, null);

            string cachedPk = Account?.PublicKey?.ToString()
                ?? PlayerPrefs.GetString(PrefKeyPublicKey, null);
            if (string.IsNullOrEmpty(cachedPk))
                throw new Exception("[MWA] Cannot sign message: no account available");

            SignedResult signedMessages = null;
            var localAssociationScenario = new LocalAssociationScenario();
            AuthorizationResult authorization = null;
            var cluster = RPCNameMap[(int)RpcCluster];
            var chain = ChainNameMap[(int)RpcCluster];
            var result = await localAssociationScenario.StartAndExecute(
                new List<Action<IAdapterOperations>>
                {
                    async client =>
                    {
                        if (_authToken.IsNullOrEmpty())
                        {
                            authorization = await client.Authorize(
                                new Uri(_walletOptions.identityUri),
                                new Uri(_walletOptions.iconUri, UriKind.Relative),
                                _walletOptions.name, cluster, chain);
                        }
                        else
                        {
                            authorization = await client.Reauthorize(
                                new Uri(_walletOptions.identityUri),
                                new Uri(_walletOptions.iconUri, UriKind.Relative),
                                _walletOptions.name, _authToken, chain);
                        }
                    },
                    async client =>
                    {
                        signedMessages = await client.SignMessages(
                            messages: new List<byte[]> { message },
                            addresses: new List<byte[]> { new PublicKey(cachedPk).KeyBytes }
                        );
                    }
                }
            );
            if (!result.WasSuccessful)
            {
                Debug.LogError(result.Error.Message);
                throw new Exception(result.Error.Message);
            }
            if (authorization == null)
            {
                throw new Exception("[MWA] SignMessage: authorization was not populated");
            }
            if (signedMessages == null)
            {
                throw new Exception("[MWA] SignMessage: signed payloads were not populated");
            }
            if (!string.IsNullOrEmpty(authorization.AuthToken))
            {
                _authToken = authorization.AuthToken;
                if (_walletOptions.keepConnectionAlive)
                {
                    PlayerPrefs.SetString(PrefKeyAuthToken, _authToken);
                    PersistChain(chain);
                    PlayerPrefs.Save();
                }
            }
            return signedMessages.SignedPayloadsBytes[0];
        }

        protected override Task<Account> _CreateAccount(string mnemonic = null, string password = null)
        {
            throw new NotImplementedException("Can't create a new account in phantom wallet");
        }
    }
}
