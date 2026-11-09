#if ENABLE_NUTAKU
using System;
using System.Collections;
using System.Collections.Generic;
using Balancy.Core;
using Balancy.Models.SmartObjects;
using Balancy.Payments;
using Newtonsoft.Json;
#if UNITY_ANDROID || UNITY_IOS
using NutakuUnitySdk;
#endif
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Scripting;
using PurchaseStatus = Balancy.Runtime.Core.PurchaseStatus;
using PurchaseResult = Balancy.Runtime.Core.PurchaseResult;

namespace Balancy.Platforms.Nutaku
{
    /// <summary>One entry of the server answer to Balancy.API.NutakuPendingPurchases.</summary>
    [Preserve]
    internal class NutakuServerPurchase
    {
        public const int StatusPending = 1;

        [JsonProperty("guid")] [Preserve] public string Guid;
        [JsonProperty("status")] [Preserve] public int Status;
        [JsonProperty("orderId")] [Preserve] public string OrderId;
        [JsonProperty("items")] [Preserve] public string[] Items;
    }

    public class NutakuPurchaseSystem : IBalancyPaymentSystem
    {
        /// <summary>Store name written into the PendingPurchaseManager records owned by this module.</summary>
        public const string StoreName = "Nutaku";

        /// <summary>
        /// A purchase is pending on the server but no local record says what the player was buying (reinstall,
        /// another device, cleared data). Arguments: Balancy product id, Nutaku payment id. Return the descriptor to
        /// grant (for example new Actions.BalancyProductInfo(storeItem)) or null to leave the purchase for later.
        /// Default: the store item sold for that product id, when there is exactly one.
        /// </summary>
        public static Func<string, string, Actions.BalancyProductInfo> ResolveUnclaimedPurchase;

        /// <summary>
        /// Raised after a purchase left pending by a previous session was completed and granted: (product id, payment id).
        /// The regular Balancy.Callbacks.OnHardPurchased* events fire as well; the callback given to
        /// API.InitPurchase* in that previous session is gone, so the game must grant from those events.
        /// </summary>
        public static event Action<string, string> PurchaseRecovered;

#if UNITY_EDITOR
        // The editor never talks to Nutaku: purchases are reported without server validation.
        private const bool RequireValidation = false;
#else
        private const bool RequireValidation = true;
#endif

        private bool _isInitialized;
        private bool _debugMode = true;
        private Func<MonoBehaviour> _getScript;
        private Func<int> _getUserId;
        // Payment ids being completed / validated in this session: a deep link, a focus change and the start-up pass
        // may all report the same order; only one of them may grant it.
        private readonly HashSet<string> _ordersInProgress = new HashSet<string>();
        private bool _reconciling;
        private bool _reconcileRequested;
#if UNITY_WEBGL
        private UnityAction<string, UnityAction<object>> _addRequest;
        private WebGLPurchase _onPurchase;
#endif
        
#if UNITY_ANDROID || UNITY_IOS
        private static NutakuPayment _lastPayment;
        private static UnityAction<NutakuPaymentData> _onLastResponse;
#endif

        internal NutakuPurchaseSystem(
            Func<MonoBehaviour> getScript,
            Func<int> getUserId
#if UNITY_WEBGL
            ,UnityAction<string, UnityAction<object>> addRequest,
            WebGLPurchase onPurchase
#endif
        )
        {
            _getScript = getScript;
            _getUserId = getUserId;
#if UNITY_WEBGL
            _addRequest = addRequest;
            _onPurchase = onPurchase;
#endif
            BalancyPaymentManager.SetPaymentSystem(this);
        }

        public void Initialize(Action onInitialized, Action<string> onInitializeFailed)
        {
            if (_isInitialized)
            {
                onInitialized();

                return;
            }

            _isInitialized = true;
            onInitialized();
        }

        public void GetProducts(Action<List<ProductInfo>> callback)
        {
            var res = new List<ProductInfo>();
            Balancy.API.GetProducts(data =>
            {
                if (!data.Success)
                    callback(null);
                else
                {
                    foreach (var product in data.Products)
                    {
                        res.Add(new ProductInfo
                        {
                            StoreSpecificId = product.PlatformProductId,
                            ProductId = product.ProductId,
                            Type = (ProductType)product.Type,
                            IsAvailable = true,
                            RawProductData = product,
                            Metadata = new ProductMetadata
                            {
                                LocalizedDescription = product.LocalizedDescription?.Value ?? API.Localization.GetLocalizedValue(product.Description),
                                LocalizedTitle = product.LocalizedName?.Value ?? API.Localization.GetLocalizedValue(product.Name)
                            }
                        });
                    }
                    callback(res);
                }
            });
        }

        public void GetProduct(string productId, Action<ProductInfo> callback)
        {
            GetRawProductData(productId, product =>
            {
                if (product == null)
                {
                    callback(null);
                    return;
                }

                callback(new ProductInfo
                {
                    StoreSpecificId = product.PlatformProductId,
                    ProductId = product.ProductId,
                    Type = (ProductType)product.Type,
                    IsAvailable = true,
                    RawProductData = product,
                    Metadata = new ProductMetadata
                    {
                        LocalizedDescription = product.LocalizedDescription?.Value ?? API.Localization.GetLocalizedValue(product.Description),
                        LocalizedTitle = product.LocalizedName?.Value ?? API.Localization.GetLocalizedValue(product.Name)
                    }
                });
            });
        }

        private void GetRawProductData(string productId, Action<Balancy.Core.Responses.Product> callback)
        {
            Balancy.API.GetProduct(productId, data =>
            {
                if (!data.Success)
                    callback(null);
                else
                {
                    callback(data.Product);
                }
            });
        }

        public void PurchaseProduct(Actions.BalancyProductInfo productInfo)
        {
            EnsureInitialized(() =>
            {
                var productId = productInfo.ProductId;
                Log($"Initiating purchase for product: {productId}");

                PurchaseBalancyProduct(productInfo);
            }, error =>
            {
                ReportPaymentStatusToBalancy(productInfo, new NutakuPurchaseResult
                {
                    Status = PurchaseStatus.Failed,
                    ProductId = productInfo.ProductId,
                    ErrorMessage = $"Payment system not initialized: {error}"
                });
            });
        }
        
        private void NutakuCompleteInternalPurchase(int userId, string orderId, UnityAction<Balancy.Core.Responses.ResponseData> callback)
        {
            API.NutakuCompletePurchase(userId, orderId, data =>
            {
                callback(data);
            });
        }

        /// <summary>
        /// The player paid (deep link / popup result): move the server row from pending to completed, then validate
        /// and grant. The local record follows: ProcessingValidation after the completion, removed after a validated
        /// grant, so an interruption at any point is finished by ProcessPendingPurchases on the next launch.
        /// </summary>
        private void CompleteInternalPurchase(Actions.BalancyProductInfo productInfo, Balancy.Core.Responses.Product product, int userId, string paymentId)
        {
            if (!string.IsNullOrEmpty(paymentId) && !_ordersInProgress.Add(paymentId))
            {
                Log($"Payment {paymentId} is already being completed");
                return;
            }

            NutakuCompleteInternalPurchase(userId, paymentId, completeResult =>
            {
                var record = FindRecordByOrder(paymentId);
                if (completeResult.Success)
                {
                    if (record != null)
                        PendingPurchaseManager.Instance.UpdatePendingPurchase(record, paymentId, PendingStatus.ProcessingValidation);

                    ReportPaymentStatusToBalancyWithValidation(productInfo, new NutakuPurchaseResult
                    {
                        Status = PurchaseStatus.Success,
                        ProductId = product.PlatformProductId,
                        Price = product.Price,
                        OrderId = paymentId
                    }, true, (validated, removeFromPending) =>
                    {
                        ReleaseOrder(paymentId);
                        if (record != null && (validated || removeFromPending))
                            PendingPurchaseManager.Instance.RemovePendingPurchase(record);
                    });
                }
                else
                {
                    // The record stays: Nutaku's confirmation may reach the server later than the player returns to
                    // the game; the next reconcile pass completes it then.
                    ReleaseOrder(paymentId);
                    LogWarning($"Completing payment {paymentId} failed: {completeResult.ErrorMessage}");
                    ReportPaymentStatusToBalancy(productInfo, new NutakuPurchaseResult
                    {
                        Status = PurchaseStatus.Failed,
                        ProductId = product.PlatformProductId,
                        ErrorMessage = completeResult.ErrorMessage
                    });
                }
            });
        }

        private int GetUserId()
        {
            return _getUserId();
        }

        private MonoBehaviour GetHelperScript()
        {
            return _getScript();
        }
        
        private void PurchaseBalancyProduct(Actions.BalancyProductInfo p)
        {
            GetRawProductData(p.ProductId, product =>
            {
                if (product == null)
                {
                    ReportPaymentStatusToBalancy(p, new PurchaseResult
                    {
                        Status = PurchaseStatus.Failed,
                        ProductId = p.ProductId,
                        ErrorMessage = "Unknown product"
                    });
                    return;
                }

                // Written before Nutaku is involved: if the app dies while the Nutaku page is open, this is what
                // tells the next launch which offer / slot / store item the payment belongs to.
                var record = PendingPurchaseManager.Instance.AddPendingPurchase(p, null, StoreName);
                PendingPurchaseManager.Instance.UpdatePendingPurchasePrice(p.ProductId, "", (decimal)product.Price);
#if UNITY_WEBGL
                var guid = Guid.NewGuid().ToString();
                _addRequest(guid, res =>
                {
                    var data = (BalancyWebGLResponseData)res;

                    if (data.Success)
                    {
                        var pData = JsonConvert.DeserializeObject<BalancyWebGLPayment>(data.Data);
                        PendingPurchaseManager.Instance.UpdatePendingPurchase(record, pData.PaymentId, PendingStatus.WaitingForStore);
                        CompleteInternalPurchase(p, product, GetUserId(), pData.PaymentId);
                    }
                    else
                    {
                        PendingPurchaseManager.Instance.RemovePendingPurchase(record);
                        ReportPaymentStatusToBalancy(p, new PurchaseResult
                        {
                            Status = PurchaseStatus.Failed,
                            ProductId = product.PlatformProductId,
                            ErrorMessage = data.Data
                        });
                    }
                });
                _onPurchase((int)Controller.Config.Environment,
                    product.PlatformProductId,
                    product.LocalizedName?.Value ?? API.Localization.GetLocalizedValue(product.Name),
                    Mathf.RoundToInt(product.Price),
                    product.Icon,
                    product.LocalizedDescription?.Value ?? API.Localization.GetLocalizedValue(product.Description),
                    guid);
#endif
#if UNITY_ANDROID || UNITY_IOS
                MakePayment(GetUserId(), p, product, record, response =>
                {
                    if (response.Success)
                    {
                        CompleteInternalPurchase(p, product, GetUserId(), response.PaymentResult.paymentId);
                    }
                    else
                    {
                        // Cancelled or never started: nothing to recover later.
                        PendingPurchaseManager.Instance.RemovePendingPurchase(record);
                        ReportPaymentStatusToBalancy(p, new PurchaseResult
                        {
                            Status = PurchaseStatus.Failed,
                            ProductId = product.PlatformProductId,
                            ErrorMessage = response.ErrorMessage
                        });
                    }
                });
#endif
            });
        }

#if UNITY_ANDROID || UNITY_IOS
        /// <summary>
        /// Nutaku's browser flow reports back through the app deep link (…://callback/param?paymentId=…&amp;payment_tentative_status=…).
        /// The status is tentative, so it is only a trigger: the server is the source of truth.
        /// </summary>
        public static void OnPaymentResultFromBrowserCallback(string paymentId, string statusFromBrowser)
        {
            var system = NutakuNetwork.ExistingPurchaseSystem;
            // Same payment this session started and is still waiting for? Older SDKs may omit the id.
            var isCurrent = _onLastResponse != null &&
                            (_lastPayment == null || string.IsNullOrEmpty(paymentId) || _lastPayment.paymentId == paymentId);

            switch (statusFromBrowser)
            {
                case "purchase":
                    if (isCurrent)
                    {
                        var payment = _lastPayment;
                        var callback = _onLastResponse;
                        _lastPayment = null;
                        _onLastResponse = null;
                        callback.Invoke(new NutakuPaymentData { Success = true, PaymentResult = payment });
                    }
                    else
                    {
                        // Cold start: the app was killed while the Nutaku page was open, so the purchase context lives
                        // only in the pending record on disk. Balancy is not synced yet at this point (the deep link is
                        // parsed inside NutakuSdk.Initialize); BalancyPaymentManager runs the reconcile pass after the
                        // payment system is ready, and if it is ready already, run it now.
                        Debug.Log($"[BalancyPayments][Nutaku]: payment {paymentId} reported as purchased outside of a running purchase; reconciling with the server");
                        system?.RequestReconcile();
                    }
                    break;
                case "errorFromGPHS":
                case "FLOW END":
                case "cancel":
                    if (!string.IsNullOrEmpty(paymentId))
                    {
                        var record = PendingPurchaseManager.Instance.GetPendingPurchaseByTransactionId(paymentId);
                        if (record != null && record.Store == StoreName && record.Status == PendingStatus.WaitingForStore)
                            PendingPurchaseManager.Instance.RemovePendingPurchase(record);
                    }

                    if (isCurrent)
                    {
                        var callback = _onLastResponse;
                        var payment = _lastPayment;
                        _lastPayment = null;
                        _onLastResponse = null;
                        callback.Invoke(new NutakuPaymentData { Success = false, ErrorMessage = statusFromBrowser, PaymentResult = payment });
                    }
                    break;
            }
        }

        /// <summary>
        /// The player may come back from the Nutaku page through the task switcher instead of the deep link. While a
        /// browser payment is open, a regained focus asks the server whether it was paid.
        /// </summary>
        internal void OnApplicationFocusChanged(bool hasFocus)
        {
            if (!hasFocus || _lastPayment == null || !_isInitialized)
                return;

            var script = GetHelperScript();
            if (script != null)
                script.StartCoroutine(ReconcileAfterDelay(2f));
        }

        private IEnumerator ReconcileAfterDelay(float seconds)
        {
            // Give a deep link that arrives together with the focus change the chance to resolve the purchase first.
            yield return new WaitForSecondsRealtime(seconds);
            if (_lastPayment != null)
                ProcessPendingPurchases();
        }

        private void MakePayment(int userId, Actions.BalancyProductInfo p, Balancy.Core.Responses.Product productInfo, PendingPurchase record, UnityAction<NutakuPaymentData> onResponse)
        {
            GetRawProductData(productInfo.ProductId, info =>
            {
                var productId = info.PlatformProductId;
                var payment = NutakuPayment.PaymentCreationInfo(
                    productId,
                    info.LocalizedName?.Value ?? API.Localization.GetLocalizedValue(info.Name),
                    UnityEngine.Mathf.RoundToInt(info.Price),
                    productInfo.Icon,
                    info.LocalizedDescription?.Value ?? API.Localization.GetLocalizedValue(info.Description)
                );

                NutakuApi.CreatePayment(payment, GetHelperScript(), resp => { OnMakePayment(p, info, record, resp, userId, onResponse); });
            });
        }

        private void OnPutPayment(NutakuApiRawResult rawResult, int userId, NutakuPayment payment, UnityAction<NutakuPaymentData> onResponse)
        {
            try
            {
                if (rawResult.responseCode == 200)
                {
                    onResponse(new NutakuPaymentData { Success = true, PaymentResult = payment });
                }
                else if (rawResult.responseCode == 424)
                {
                    LogError("Payment ID " + rawResult.correlationId + " error caused by your Game Payment Handler Server not responding positively to the S2S PUT Request!!! ");
                    onResponse(new NutakuPaymentData { Success = false, ErrorMessage = rawResult.body, PaymentResult = payment });
                }
                else
                {
                    onResponse(new NutakuPaymentData { Success = false, ErrorMessage = rawResult.body, PaymentResult = payment });
                }
            }
            catch (Exception ex)
            {
                LogError("RequestPostPayment Failure: " + ex.Message + " => " + ex.StackTrace);
                onResponse(new NutakuPaymentData { Success = false, ErrorMessage = ex.Message, PaymentResult = payment });
            }
        }

        private void OnMakePayment(Actions.BalancyProductInfo productInfo, Balancy.Core.Responses.Product p, PendingPurchase record, NutakuApiRawResult rawResult, int userId, UnityAction<NutakuPaymentData> onResponse)
        {
            try
            {
                if ((rawResult.responseCode > 199) && (rawResult.responseCode < 300))
                {
                    var parsedResult = NutakuApi.Parse_CreatePayment(rawResult);
                    // From here on the Nutaku payment id identifies the purchase on both sides.
                    PendingPurchaseManager.Instance.UpdatePendingPurchase(record, parsedResult.paymentId, PendingStatus.WaitingForStore);

                    if (parsedResult.next == "put")
                    {
                        if (NutakuNetwork.NutakuInstance._onConfigOnConfirmPurchase != null)
                        {
                            NutakuNetwork.NutakuInstance._onConfigOnConfirmPurchase(confirmed =>
                            {
                                if (confirmed)
                                {
#if UNITY_EDITOR
                                    ReportEditorPurchase(productInfo, p, record, parsedResult.paymentId);
#else
                                    NutakuApi.PutPayment(parsedResult.paymentId, GetHelperScript(), resp => OnPutPayment(resp, userId, parsedResult, onResponse));
#endif
                                }
                                else
                                    onResponse(new NutakuPaymentData { Success = false, ErrorMessage = "cancel", PaymentResult = parsedResult });
                            });
                        }
                        else
                        {
                            onResponse(new NutakuPaymentData { Success = false, ErrorMessage = "No callback", PaymentResult = parsedResult });
                        }
                    }
                    else
                    {
                        _lastPayment = parsedResult;
                        _onLastResponse = onResponse;
#if UNITY_EDITOR
                        ReportEditorPurchase(productInfo, p, record, parsedResult.paymentId);
#else
                        NutakuSdk.OpenTransactionUrlInBrowser(parsedResult.transactionUrl);
#endif
                    }
                }
                else
                {
                    LogError("RequestPostPayment Failure: " + rawResult.responseCode + " => " + rawResult.body);
                    onResponse(new NutakuPaymentData { Success = false, ErrorMessage = rawResult.body });
                }
            }
            catch (Exception ex)
            {
                LogError("RequestPostPayment Failure: " + ex.Message + " => " + ex.StackTrace);
                onResponse(new NutakuPaymentData { Success = false, ErrorMessage = ex.Message });
            }
        }

#if UNITY_EDITOR
        private void ReportEditorPurchase(Actions.BalancyProductInfo productInfo, Balancy.Core.Responses.Product p, PendingPurchase record, string paymentId)
        {
            _lastPayment = null;
            _onLastResponse = null;
            ReportPaymentStatusToBalancyWithValidation(productInfo, new PurchaseResult
            {
                Status = PurchaseStatus.Success,
                ProductId = productInfo.ProductId,
                Receipt = new Balancy.Runtime.Core.PurchaseReceipt { Receipt = "", ProductId = productInfo.ProductId, TransactionId = paymentId },
                Price = Mathf.RoundToInt(p.Price),
                TransactionId = paymentId,
            }, false);
            PendingPurchaseManager.Instance.RemovePendingPurchase(record);
        }
#endif
#endif

        public bool IsPurchasingSupported()
        {
            return IsInitialized();
        }

        public bool IsInitialized()
        {
            return _isInitialized;
        }

        /// <summary>Run the reconcile pass now if the payment system is ready; otherwise it runs after initialization.</summary>
        internal void RequestReconcile()
        {
            if (_isInitialized)
                ProcessPendingPurchases();
        }

        /// <summary>
        /// Finish purchases interrupted in a previous session. Called by BalancyPaymentManager once the payment
        /// system is initialized and the cloud profile is synced (and again from the deep link / focus triggers):
        /// 1. local records already completed on the server (ProcessingValidation) are validated and granted;
        /// 2. the server list of pending Nutaku purchases is fetched; every entry is matched to a local record by
        ///    payment id, then by product (WebGL learns the payment id only after the popup), then resolved through
        ///    ResolveUnclaimedPurchase; each is completed, validated and granted, and its record removed.
        /// </summary>
        public void ProcessPendingPurchases()
        {
            if (_reconciling)
            {
                _reconcileRequested = true;
                return;
            }

            _reconciling = true;
            var manager = PendingPurchaseManager.Instance;

            foreach (var record in manager.GetAllPendingPurchases())
            {
                if (!IsOurs(record) || record.Status != PendingStatus.ProcessingValidation || string.IsNullOrEmpty(record.TransactionId))
                    continue;

                Log($"Re-validating payment {record.TransactionId} for {record.ProductInfo?.ProductId}");
                ValidateRecovered(record);
            }

            API.NutakuPendingPurchases(response =>
            {
                try
                {
                    if (response.Success)
                        ReconcileServerPending(response.Data);
                    else
                        LogWarning($"Could not list pending purchases: {response.ErrorMessage}");
                }
                catch (Exception exception)
                {
                    LogError($"Reconcile failed: {exception}");
                }
                finally
                {
                    _reconciling = false;
                    if (_reconcileRequested)
                    {
                        _reconcileRequested = false;
                        ProcessPendingPurchases();
                    }
                }
            });
        }

        private void ReconcileServerPending(string json)
        {
            List<NutakuServerPurchase> rows;
            try
            {
                rows = string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<List<NutakuServerPurchase>>(json);
            }
            catch (JsonException exception)
            {
                LogError($"Pending purchases answer is not a list: {exception.Message}");
                return;
            }

            if (rows == null || rows.Count == 0)
                return;

            var manager = PendingPurchaseManager.Instance;
            foreach (var row in rows)
            {
                if (row == null || row.Status != NutakuServerPurchase.StatusPending || string.IsNullOrEmpty(row.OrderId))
                    continue;
                if (_ordersInProgress.Contains(row.OrderId))
                    continue;

                var productId = row.Items != null && row.Items.Length > 0 ? row.Items[0] : null;

#if UNITY_ANDROID || UNITY_IOS
                // The purchase this session is still waiting for (the player came back without the deep link):
                // resolve it through the regular path so the InitPurchase* callback gets its answer.
                if (_onLastResponse != null && _lastPayment != null && _lastPayment.paymentId == row.OrderId)
                {
                    var payment = _lastPayment;
                    var callback = _onLastResponse;
                    _lastPayment = null;
                    _onLastResponse = null;
                    callback.Invoke(new NutakuPaymentData { Success = true, PaymentResult = payment });
                    continue;
                }
#endif

                var record = FindRecordByOrder(row.OrderId);
                if (record != null && record.Status == PendingStatus.ProcessingValidation)
                    continue; // completed earlier in this pass; the validation is on its way

                if (record == null && !string.IsNullOrEmpty(productId))
                {
                    // WebGL writes the record before the payment id is known: adopt the oldest one of this product.
                    record = FindUnassignedRecord(productId);
                    if (record != null)
                        manager.UpdatePendingPurchase(record, row.OrderId, PendingStatus.WaitingForStore);
                }

                if (record == null)
                {
                    var productInfo = ResolveUnclaimed(productId, row.OrderId);
                    if (productInfo == null)
                    {
                        LogWarning($"Payment {row.OrderId} for product '{productId}' is pending on the server but nothing describes it locally; set NutakuPurchaseSystem.ResolveUnclaimedPurchase to claim it");
                        continue;
                    }

                    record = manager.AddPendingPurchase(productInfo, row.OrderId, StoreName);
                }

                CompleteRecovered(record);
            }
        }

        private void CompleteRecovered(PendingPurchase record)
        {
            var orderId = record.TransactionId;
            if (!_ordersInProgress.Add(orderId))
                return;

            Log($"Completing payment {orderId} for {record.ProductInfo?.ProductId} left from a previous session");
            NutakuCompleteInternalPurchase(GetUserId(), orderId, completeResult =>
            {
                if (!completeResult.Success)
                {
                    ReleaseOrder(orderId);
                    LogWarning($"Completing payment {orderId} failed: {completeResult.ErrorMessage}; it will be retried on the next launch");
                    return;
                }

                PendingPurchaseManager.Instance.UpdatePendingPurchase(record, orderId, PendingStatus.ProcessingValidation);
                ValidateRecovered(record, true);
            });
        }

        private void ValidateRecovered(PendingPurchase record, bool orderAlreadyTaken = false)
        {
            var orderId = record.TransactionId;
            if (!orderAlreadyTaken && !_ordersInProgress.Add(orderId))
                return;

            var productInfo = record.ProductInfo;
            GetRawProductData(productInfo.ProductId, product =>
            {
                ReportPaymentStatusToBalancyWithValidation(productInfo, new NutakuPurchaseResult
                {
                    Status = PurchaseStatus.Success,
                    ProductId = product?.PlatformProductId ?? productInfo.ProductId,
                    Price = product?.Price ?? record.Price,
                    OrderId = orderId
                }, RequireValidation, (validated, removeFromPending) =>
                {
                    ReleaseOrder(orderId);
                    if (validated || removeFromPending)
                        PendingPurchaseManager.Instance.RemovePendingPurchase(record);

                    if (validated)
                    {
                        Log($"Recovered payment {orderId} for {productInfo.ProductId}");
                        try { PurchaseRecovered?.Invoke(productInfo.ProductId, orderId); }
                        catch (Exception exception) { Debug.LogException(exception); }
                    }
                    else
                        LogWarning($"Validation of payment {orderId} failed" + (removeFromPending ? " permanently" : "; it will be retried on the next launch"));
                });
            });
        }

        private static Actions.BalancyProductInfo ResolveUnclaimed(string productId, string orderId)
        {
            var resolver = ResolveUnclaimedPurchase;
            if (resolver != null)
            {
                try { return resolver(productId, orderId); }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    return null;
                }
            }

            return DefaultResolveUnclaimed(productId);
        }

        /// <summary>Falls back to the store item sold for the product; offers of a previous session cannot be rebuilt.</summary>
        private static Actions.BalancyProductInfo DefaultResolveUnclaimed(string productId)
        {
            if (string.IsNullOrEmpty(productId))
                return null;

            StoreItem match = null;
            var count = 0;
            foreach (var storeItem in CMS.GetModels<StoreItem>(true))
            {
                if (storeItem?.Price?.Product?.ProductId != productId)
                    continue;
                count++;
                if (match == null)
                    match = storeItem;
            }

            if (match == null)
                return null;
            if (count > 1)
                Debug.LogWarning($"[BalancyPayments][Nutaku]: {count} store items sell product '{productId}'; granting '{match.UnnyId}'. Set NutakuPurchaseSystem.ResolveUnclaimedPurchase to choose.");

            return new Actions.BalancyProductInfo(match);
        }

        private static bool IsOurs(PendingPurchase record)
        {
            return record != null && record.Store == StoreName && record.ProductInfo != null;
        }

        private static PendingPurchase FindRecordByOrder(string orderId)
        {
            if (string.IsNullOrEmpty(orderId))
                return null;
            var record = PendingPurchaseManager.Instance.GetPendingPurchaseByTransactionId(orderId);
            return IsOurs(record) ? record : null;
        }

        private static PendingPurchase FindUnassignedRecord(string productId)
        {
            PendingPurchase oldest = null;
            foreach (var record in PendingPurchaseManager.Instance.GetAllPendingPurchases())
            {
                if (!IsOurs(record) || record.Status != PendingStatus.WaitingForStore || !string.IsNullOrEmpty(record.TransactionId))
                    continue;
                if (record.ProductInfo.ProductId != productId)
                    continue;
                if (oldest == null || record.Timestamp < oldest.Timestamp)
                    oldest = record;
            }

            return oldest;
        }

        private void ReleaseOrder(string orderId)
        {
            if (!string.IsNullOrEmpty(orderId))
                _ordersInProgress.Remove(orderId);
        }

        public void ReportPaymentStatusToBalancyWithValidation(Actions.BalancyProductInfo productInfo, Balancy.Runtime.Core.PurchaseResult result, bool requireReceiptValidation = true)
        {
            ReportPaymentStatusToBalancyWithValidation(productInfo, new NutakuPurchaseResult
            {
                ProductId = result.ProductId,
                ErrorMessage = result.ErrorMessage,
                OrderId = result.TransactionId,
                Status = result.Status,
                Price = result.Price,
                CurrencyCode = result.CurrencyCode
            }, requireReceiptValidation);
        }
        
        public void ReportPaymentStatusToBalancy(Actions.BalancyProductInfo productInfo, Balancy.Runtime.Core.PurchaseResult result)
        {
            ReportPaymentStatusToBalancyWithValidation(productInfo, new NutakuPurchaseResult
            {
                ProductId = result.ProductId,
                ErrorMessage = result.ErrorMessage,
                OrderId = result.TransactionId,
                Status = result.Status,
                Price = result.Price,
                CurrencyCode = result.CurrencyCode
            });
        }

        private static Actions.PurchaseResult ConvertStatusToResult(Balancy.Runtime.Core.PurchaseStatus status)
        {
            switch (status)
            {
                case PurchaseStatus.Success:
                    return Actions.PurchaseResult.Success;
                case PurchaseStatus.Failed:
                case PurchaseStatus.AlreadyOwned:
                case PurchaseStatus.InvalidProduct:
                    return Actions.PurchaseResult.Failed;
                case PurchaseStatus.Pending:
                    return Actions.PurchaseResult.Pending;
                case PurchaseStatus.Cancelled:
                    return Actions.PurchaseResult.Cancelled;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, null);
            }
        }
        
        private void ReportPaymentStatusToBalancyWithValidation(Actions.BalancyProductInfo productInfo, NutakuPurchaseResult result, bool requireReceiptValidation = true, Action<bool, bool> onValidated = null)
        {
            var paymentInfo = new PaymentInfo
            {
                ProductId = productInfo?.ProductId,
                Currency = result.CurrencyCode ?? "",
                Price = result.Price,
                Receipt = "<receipt>",
                OrderId = result.OrderId,
            };
                
            Balancy.API.FinalizedHardPurchase(ConvertStatusToResult(result.Status), productInfo, paymentInfo, (validated, removeFromPending) =>
            {
                onValidated?.Invoke(validated, removeFromPending);
            }, requireReceiptValidation);
        }

        private void ReportPaymentStatusToBalancy(Actions.BalancyProductInfo productInfo, NutakuPurchaseResult result)
        {
            ReportPaymentStatusToBalancyWithValidation(productInfo, result);
        }
        
        private void EnsureInitialized(Action onInitialized, Action<string> onFailed)
        {
            if (IsInitialized())
            {
                onInitialized?.Invoke();
            }
            else
            {
                Initialize(onInitialized, onFailed);
            }
        }
        
        public void FinishTransaction(string productId, string transactionId)
        {
            throw new NotImplementedException();
        }

        public void RestorePurchases(Action<List<Balancy.Runtime.Core.PurchaseResult>> onRestoreComplete)
        {
            
        }

        public void GetSubscriptionsInfo(Action<List<Balancy.Runtime.Core.SubscriptionInfo>> callback)
        {
            
        }

        private void Log(string message)
        {
            if (_debugMode)
            {
                Debug.Log($"[BalancyPayments][Nutaku]: {message}");
            }
        }

        private void LogWarning(string message)
        {
            if (_debugMode)
            {
                Debug.LogWarning($"[BalancyPayments][Nutaku]: {message}");
            }
        }

        private void LogError(string message)
        {
            if (_debugMode)
            {
                Debug.LogError($"[BalancyPayments][Nutaku]: {message}");
            }
        }
    }
}
#endif
