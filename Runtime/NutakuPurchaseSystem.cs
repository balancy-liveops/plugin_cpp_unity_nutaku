#if ENABLE_NUTAKU
using System;
using System.Collections.Generic;
using Balancy.Core;
using Balancy.Payments;
using Newtonsoft.Json;
#if UNITY_ANDROID || UNITY_IOS
using NutakuUnitySdk;
#endif
using UnityEngine;
using UnityEngine.Events;
using PurchaseStatus = Balancy.Runtime.Core.PurchaseStatus;
using PurchaseResult = Balancy.Runtime.Core.PurchaseResult;

namespace Balancy.Platforms.Nutaku
{
    public class NutakuPurchaseSystem : IBalancyPaymentSystem
    {
        private bool _isInitialized;
        private bool _debugMode = true;
        private Func<MonoBehaviour> _getScript;
        private Func<int> _getUserId;
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

            // Actions.Purchasing.SetHardPurchaseCallback(TryToHardPurchase);
            _isInitialized = true;
            onInitialized();
        }

        // private void TryToHardPurchase(Actions.BalancyProductInfo productInfo)
        // {
        //     var productId = productInfo?.ProductId;
        //
        //     if (string.IsNullOrEmpty(productId))
        //     {
        //         API.FinalizedHardPurchase(Actions.PurchaseResult.Failed, productInfo, null, null);
        //         return;
        //     }
        //
        //     PurchaseProduct(productInfo);
        // }

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

        private void CompleteInternalPurchase(Actions.BalancyProductInfo productInfo, Balancy.Core.Responses.Product product, int userId, string paymentId)
        {
            NutakuCompleteInternalPurchase(userId, paymentId, completeResult =>
            {
                if (completeResult.Success)
                {
                    ReportPaymentStatusToBalancy(productInfo, new NutakuPurchaseResult
                    {
                        Status = PurchaseStatus.Success,
                        ProductId = product.PlatformProductId,
                        Price = product.Price,
                        OrderId = paymentId
                    });
                    
                    // var successResponse = new Responses.PurchaseProductResponseData
                    // {
                    //     Success = true,
                    //     ProductId = 000,
                    // };
                    //
                    // successResponse.Data = new PurchaseData
                    // {
                    //     Id = product.PlatformProductId,
                    //     BaseProductId = product.ProductId,
                    //     Items = completeResult.Data.Items,
                    //     PriceBalancy = product.Price,
                    //     PriceUSD = product.Price,
                    //     Time = completeResult.Data.Time
                    // };
                    // callback(successResponse);
                }
                else
                {
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
#if UNITY_WEBGL
            var guid = Guid.NewGuid().ToString();
            _addRequest(guid, res =>
            {
                var data = (BalancyWebGLResponseData)res;

                if (data.Success)
                {
                    var pData = JsonConvert.DeserializeObject<BalancyWebGLPayment>(data.Data);
                    CompleteInternalPurchase(p, product, GetUserId(), pData.PaymentId);
                }
                else
                {
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
                MakePayment(GetUserId(), p, product, response =>
                {
                    if (response.Success)
                    {
                        CompleteInternalPurchase(p, product, GetUserId(), response.PaymentResult.paymentId);
                    }
                    else
                    {
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
        
        // private void OnUpdateProductsInfo(Product[] products, Action<bool> callback)
        // {
        //     foreach (var product in products)
        //     {
        //         product.Meta = new ProductMetaData
        //         {
        //             LocalizedPriceString = product.Price.ToString(CultureInfo.InvariantCulture),
        //             LocalizedTitle = product.LocalizedName?.Value ?? Localization.Manager.Get(product.Name),
        //             LocalizedDescription = product.LocalizedDescription?.Value ??
        //                                    Localization.Manager.Get(product.Description),
        //             IsoCurrencyCode = "",
        //             LocalizedPrice = (decimal)product.Price
        //         };
        //     }
        //
        //     callback?.Invoke(true);
        // }
        
#if UNITY_ANDROID || UNITY_IOS
        public static void OnPaymentResultFromBrowserCallback(string paymentId, string statusFromBrowser)
        {
            switch (statusFromBrowser)
            {
                case "purchase":
                    _onLastResponse?.Invoke(new NutakuPaymentData { Success = true, PaymentResult = _lastPayment });
                    break;
                case "errorFromGPHS":
                case "FLOW END":
                case "cancel":
                    _onLastResponse?.Invoke(new NutakuPaymentData { Success = false, ErrorMessage = statusFromBrowser});
                    break;
            }
        }

        private void MakePayment(int userId, Actions.BalancyProductInfo p, Balancy.Core.Responses.Product productInfo, UnityAction<NutakuPaymentData> onResponse)
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

                NutakuApi.CreatePayment(payment, GetHelperScript(), resp => { OnMakePayment(p, info, resp, userId, onResponse); });
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
                    onResponse(new NutakuPaymentData { Success = false, ErrorMessage = rawResult.body});
                }
                else
                {
                    onResponse(new NutakuPaymentData { Success = false, ErrorMessage = rawResult.body});
                }
            }
            catch (Exception ex)
            {
                LogError("RequestPostPayment Failure: " + ex.Message + " => " + ex.StackTrace);
                onResponse(new NutakuPaymentData { Success = false, ErrorMessage = ex.Message});
            }
        }

        private void OnMakePayment(Actions.BalancyProductInfo productInfo,Balancy.Core.Responses.Product p, NutakuApiRawResult rawResult, int userId, UnityAction<NutakuPaymentData> onResponse)
        {
            try
            {
                if ((rawResult.responseCode > 199) && (rawResult.responseCode < 300))
                {
                    var parsedResult = NutakuApi.Parse_CreatePayment(rawResult);
                    if (parsedResult.next == "put")
                    {
                        if (NutakuNetwork.NutakuInstance._onConfigOnConfirmPurchase != null)
                        {
                            NutakuNetwork.NutakuInstance._onConfigOnConfirmPurchase(confirmed =>
                            {
                                if (confirmed)
                                {
#if UNITY_EDITOR
                                    ReportPaymentStatusToBalancyWithValidation(productInfo, new PurchaseResult
                                    {
                                        Status = PurchaseStatus.Success,
                                        ProductId = productInfo.ProductId,
                                        Receipt = new Balancy.Runtime.Core.PurchaseReceipt { Receipt = "", ProductId = productInfo.ProductId, TransactionId = parsedResult.paymentId},
                                        Price = Mathf.RoundToInt(p.Price),
                                        TransactionId = parsedResult.paymentId,
                                    }, false);
#else
                                    NutakuApi.PutPayment(parsedResult.paymentId, GetHelperScript(), resp => OnPutPayment(resp, userId, parsedResult, onResponse));
#endif
                                }
                                else
                                    onResponse(new NutakuPaymentData { Success = false, ErrorMessage = "cancel"});
                            });
                        }
                        else
                        {
                            onResponse(new NutakuPaymentData { Success = false, ErrorMessage = "No callback"});
                        }
                    }
                    else
                    {
                        _lastPayment = parsedResult;
                        _onLastResponse = onResponse;
#if UNITY_EDITOR
                        ReportPaymentStatusToBalancyWithValidation(productInfo, new PurchaseResult
                        {
                            Status = PurchaseStatus.Success,
                            ProductId = productInfo.ProductId,
                            Receipt = new Balancy.Runtime.Core.PurchaseReceipt { Receipt = "", ProductId = productInfo.ProductId, TransactionId = parsedResult.paymentId},
                            Price = Mathf.RoundToInt(p.Price),
                            TransactionId = parsedResult.paymentId,
                        }, false);
#else
                        NutakuSdk.OpenTransactionUrlInBrowser(parsedResult.transactionUrl);
#endif
                    }
                }
                else
                {
                    LogError("RequestPostPayment Failure: " + rawResult.responseCode + " => " + rawResult.body);
                    onResponse(new NutakuPaymentData { Success = false, ErrorMessage = rawResult.body});
                }
            }
            catch (Exception ex)
            {
                LogError("RequestPostPayment Failure: " + ex.Message + " => " + ex.StackTrace);
                onResponse(new NutakuPaymentData { Success = false, ErrorMessage = ex.Message});
            }
        }
#endif

        public bool IsPurchasingSupported()
        {
            return IsInitialized();
        }

        public bool IsInitialized()
        {
            return _isInitialized;
        }

        public void ProcessPendingPurchases()
        {
            
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
        
        private void ReportPaymentStatusToBalancyWithValidation(Actions.BalancyProductInfo productInfo, NutakuPurchaseResult result, bool requireReceiptValidation = true)
        {
            var paymentInfo = new PaymentInfo
            {
                ProductId = productInfo?.ProductId,
                Currency = result.CurrencyCode ?? "",
                Price = result.Price,
                Receipt = "<receipt>",
                OrderId = result.OrderId,
            };
                
            Balancy.API.FinalizedHardPurchase(ConvertStatusToResult(result.Status), productInfo, paymentInfo, (_, _) =>
            {
                // should I do something here?
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