#if ENABLE_NUTAKU
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Balancy.Core;
using Balancy.Data.SmartObjects;
#if UNITY_WEBGL
using System.Runtime.InteropServices;
using UnityEngine.Scripting;
#endif
using Newtonsoft.Json;
#if UNITY_ANDROID
using NutakuUnitySdk;
#endif
using UnityEngine;
using UnityEngine.Events;

namespace Balancy.Platforms.Nutaku
{
    public delegate void WebGLPurchase(int env, string id, string name, int price, string img, string desc, string req_id);
    
    public class UserInfo
    {
        [JsonProperty("id")]
        public int UserId { get; set; }

        [JsonProperty("nickname")]
        public string NickName { get; set; }
    }
    
    public class NutakuPurchaseResult
    {
        public Balancy.Payments.PurchaseStatus Status { get; set; }
        public string ProductId { get; set; }
        public string ErrorMessage { get; set; }
        
        public string CurrencyCode { get; set; }
        public float Price { get; set; }
        
        public string OrderId { get; set; }
    }
    
#if UNITY_ANDROID
    [Serializable]
    public class HandshakeResponse
    {
        [JsonProperty]
        public string token;
    }
    
    public class NutakuPaymentData : Responses.ResponseData
    {
        private NutakuPayment paymentResult;

        public NutakuPayment PaymentResult
        {
            get { return paymentResult; }
            set { paymentResult = value; }
        }
    }
#endif
    public class NutakuNetwork
    {
        private UserInfo _user;
        private static BalancyNutakuObject _gameObjectHelper;
        
#if UNITY_WEBGL
        public class TokensInfo
        {
            [JsonProperty("token")] public string Token;

            [JsonProperty("id")] public string UserId;
        }

        [DllImport("__Internal")]
        private static extern void purchase(int env, string id, string name, int price, string img, string desc, string req_id);

        [DllImport("__Internal")]
        private static extern void handshake(string req_id);

        [DllImport("__Internal")]
        private static extern void getRealUserId(string req_id);

        public UserInfo GetUserInfo()
        {
            if (_user == null)
            {
                var guid = Guid.NewGuid().ToString();
                _requests.Add(guid, res =>
                {
                    var data = (BalancyWebGLResponseData)res;
                    if (data.Success)
                    {
                        var obj = JsonConvert.DeserializeObject<UserInfo>(data.Data);
                        _user = obj;
                    }
                    else
                    {
                        Debug.LogError("GetUserId: cant get user id " + data.Data);
                    }
                });

                getRealUserId(guid);
            }

            return _user;
        }

        public int GetUserId()
        {
            if (_user == null)
            {
                GetUserInfo();
            }

            return _user?.UserId ?? 0;
        }

        private Dictionary<string, UnityAction<object>> _requests = new Dictionary<string, UnityAction<object>>();
#endif
#if UNITY_ANDROID
        private delegate void OnHandshakeDelegate(NutakuApiRawResult rawResult, Constants.BalancyPlatform platform, UnityAction<Responses.AuthResponseData> onResult);
        
        internal Action<Action<bool>> _onConfigOnConfirmPurchase;
        private static UnityAction<Responses.AuthResponseData> _onLoginResult;
        private static OnHandshakeDelegate _onHandshake;

		public UserInfo GetUserInfo()
		{
			return _user;
		}

		public int GetUserId()
		{
			if (_user != null && _user.UserId != 0)
				return _user.UserId;

			return 0;
		}

        internal void RetrieveProfile()
        {
            _user = new UserInfo { UserId = NutakuCurrentUser.GetUserId(), NickName = NutakuCurrentUser.GetUserNickname() };
        }
        
        public void SetOnConfirmPurchaseCallback(Action<Action<bool>> cb)
        {
            _onConfigOnConfirmPurchase = cb;
        }
#endif

        private static NutakuNetwork _nutakuInstance;
        private NutakuPurchaseSystem _system;
        
        [RuntimeInitializeOnLoadMethod]
        private static void Init()
        {
            if (NutakuInstance._system != null)
            {
                Debug.Log("[BalancyPayments] Nutaku: start initing");
            }
        }

        public static NutakuNetwork NutakuInstance
        {
            get
            {
                if (_nutakuInstance == null)
                {
                    _nutakuInstance = new NutakuNetwork();

                    _nutakuInstance._system = new NutakuPurchaseSystem(
                        _nutakuInstance.GetScript,
                        _nutakuInstance.GetUserId
#if UNITY_WEBGL
                        ,_nutakuInstance.AddRequest,
                        purchase
#endif
                    );
                }

                return _nutakuInstance;
            }
        }

#if UNITY_WEBGL
        private void AddRequest(string key, UnityAction<object> unityAction)
        {
            _requests.Add(key, unityAction);
        }
#endif

        private MonoBehaviour GetScript()
        {
            return _gameObjectHelper;
        }

        private NutakuNetwork()
        {
            Debug.LogWarning("NutakuNetwork");
            _nutakuInstance = this;

#if UNITY_ANDROID
            _onHandshake = OnHandshake;
            NutakuSdkConfig.loginResultToGameCallbackDelegate = OnLoginResultCallback;
            NutakuSdkConfig.paymentBrowserResultToGameCallbackDelegate = OnPaymentResultFromBrowserCallback;
#endif
            // Payments.InvalidateCache();

            var gameObjectHelper = new GameObject("BalancyNutakuObject");
            UnityEngine.Object.DontDestroyOnLoad(gameObjectHelper);
            var script = gameObjectHelper.AddComponent<BalancyNutakuObject>();
            _gameObjectHelper = script;
            script.SetScript(script);
#if UNITY_WEBGL
            var gameObject = new GameObject("BalancyWebGLObject");
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            gameObject.AddComponent<BalancyWebGLObject>();
#endif
        }

        private void CreateAuthRequest(string userId, Constants.BalancyPlatform platform, string oauthToken, Action<Responses.AuthResponseData> doneCallback)
        {
            Debug.LogWarning("CreateAuthRequest: " + userId);
            API.Auth.WithNutaku(userId, oauthToken, data =>
            {
                Debug.LogWarning("WithNutaku: " + userId);
                doneCallback?.Invoke(data);
            });
        }

        public void Login(UnityAction<Responses.AuthResponseData> onResult)
        {
#if UNITY_WEBGL
            var guid = Guid.NewGuid().ToString();
            _requests.Add(guid, res =>
            {
                GetUserId();

                var data = (BalancyWebGLResponseData)res;
                if (data.Success)
                {
                    var obj = JsonConvert.DeserializeObject<TokensInfo>(data.Data);
                    _nutakuInstance.CreateAuthRequest(obj.UserId, Controller.Config.BalancyPlatform, obj.Token, (authResp) =>
                    {
                        if (authResp.Success)
                        {
                            onResult(authResp);
                        }
                        else
                        {
                            onResult(new Responses.AuthResponseData { ErrorMessage = data.Data, Success = false });
                        }
                    });
                }
                else
                {
                    Debug.LogError("OnAuth error: " + data.Data);
                    onResult(new Responses.AuthResponseData { ErrorMessage = data.Data, Success = false });
                }
            });

            handshake(guid);
#endif
#if UNITY_ANDROID
            _onLoginResult = onResult;
            NutakuSdk.Initialize(_gameObjectHelper);
#endif
        }
#if UNITY_ANDROID
        private void OnHandshake(NutakuApiRawResult rawResult, Constants.BalancyPlatform platform, UnityAction<Responses.AuthResponseData> onResult)
        {
            Debug.LogWarning("OnHandshake: " + rawResult.responseCode);
            try
            {
                if ((rawResult.responseCode > 199) && (rawResult.responseCode < 300))
                {
                    var parsedResult = NutakuApi.Parse_GameHandshake(rawResult);
                    if (parsedResult.game_rc == 0)
                    {
                        onResult(new Responses.AuthResponseData { ErrorMessage = parsedResult.message, Success = false});
                    }
                    else
                    {
                        Debug.LogWarning("GetUserId(): " + NutakuCurrentUser.GetUserId());
                        _gameObjectHelper.WaitUntil(() => NutakuCurrentUser.GetUserId() != 0, () =>
                        {
                            var data = JsonConvert.DeserializeObject<HandshakeResponse>(parsedResult.message);

                            _nutakuInstance.RetrieveProfile();

                            AuthNutaku(GetUserId().ToString(), platform, data.token, (authResp) =>
                            {
                                if (authResp.Success)
                                {
                                    onResult(authResp);
                                }
                                else
                                {
                                    onResult(new Responses.AuthResponseData { ErrorMessage = authResp.ErrorMessage, Success = false});
                                }
                            });
                        });
                    }
                }
                else
                {
                    onResult(new Responses.AuthResponseData { ErrorMessage = rawResult.body, Success = false});
                }
            }
            catch (Exception ex)
            {
                onResult(new Responses.AuthResponseData { ErrorMessage = ex.Message, Success = false } );
            }
        }

        public static void OnLoginResultCallback(bool wasSuccess)
        {
            Debug.LogWarning("OnLoginResultCallback: " + wasSuccess);
            if (wasSuccess)
                NutakuApi.GameHandshake(_gameObjectHelper, result => _onHandshake(result, Controller.Config.BalancyPlatform, _onLoginResult));
            else
            {
                _onLoginResult(new Responses.AuthResponseData { ErrorMessage = "can not login", Success = false });
            }
        }
        
        public static void OnPaymentResultFromBrowserCallback(string paymentId, string statusFromBrowser)
        {
            NutakuPurchaseSystem.OnPaymentResultFromBrowserCallback(paymentId, statusFromBrowser);
        }
        
        private void AuthNutaku(string userId, Constants.BalancyPlatform platform, string oauthToken, Action<Responses.AuthResponseData> doneCallback)
        {
            Debug.LogWarning("AuthNutaku: " + userId + " => " + oauthToken);
            CreateAuthRequest(userId, platform, oauthToken, doneCallback);
        }
#endif

#if UNITY_WEBGL
        internal void OnWebGLMessage(string reqId, object obj)
        {
            if (_requests.TryGetValue(reqId, out UnityAction<object> callBack))
            {
                callBack?.Invoke(obj);
                _requests.Remove(reqId);
            }
        }
#endif
    }


#if UNITY_WEBGL
    [Preserve]
    internal class BalancyWebGLObjectResponse
    {
        [JsonProperty("req_id")]
        [Preserve]
        public string RequestId;

        [JsonProperty("response")]
        [Preserve]
        public BalancyWebGLResponseData Response;
    }

    internal class BalancyWebGLResponseData
    {
        [JsonProperty("success")] public bool Success;

        [JsonProperty("data")] public string Data;
    }

    public class BalancyWebGLPayment
    {
        [JsonProperty("payment_id")] public string PaymentId;
    }

    public class BalancyWebGLObject : MonoBehaviour
    {
        public void OnPurchase(string response)
        {
            var obj = JsonConvert.DeserializeObject<BalancyWebGLObjectResponse>(response);
            NutakuNetwork.NutakuInstance.OnWebGLMessage(obj.RequestId, obj.Response);
        }

        public void OnHandshake(string response)
        {
            var obj = JsonConvert.DeserializeObject<BalancyWebGLObjectResponse>(response);
            NutakuNetwork.NutakuInstance.OnWebGLMessage(obj.RequestId, obj.Response);
        }

        public void OnUserId(string response)
        {
            var obj = JsonConvert.DeserializeObject<BalancyWebGLObjectResponse>(response);
            NutakuNetwork.NutakuInstance.OnWebGLMessage(obj.RequestId, obj.Response);
        }
    }
#endif
    
    internal class BalancyNutakuObject : MonoBehaviour
    {
        private static readonly WaitForFixedUpdate FixedUpdate = new WaitForFixedUpdate();
        private static MonoBehaviour _script;

        internal void SetScript(MonoBehaviour script)
        {
            _script = script;
        }
        
        internal Coroutine WaitUntil(Func<bool> condition, Action callback)
        {
            return _script.StartCoroutine(WaitUntilInternal(condition, callback));
        }
        
        private static IEnumerator WaitUntilInternal(Func<bool> condition, Action callback)
        {
            while (!condition())
            {
                yield return FixedUpdate;
            }

            callback?.Invoke();
        }
    }
}

#endif