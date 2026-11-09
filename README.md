# Balancy Nutaku integration for Unity

Module provides method to work with Nutaku's purchases system, authorisation, etc.

## Installation

### Via OpenUPM

The recommended way to install through OpenUPM:

```bash
openupm add co.balancy.nutaku
```

### Via Git URL

You can also install this package through the Unity Package Manager using Git:

1. Open **Unity Package Manager**  
2. Click the **“+”** button  
3. Select **“Add package from git URL”**  
4. Enter:  
   ```
   https://github.com/balancy-liveops/plugin_cpp_unity_nutaku.git
   ```

## Getting Started

1. Make sure Balancy Purchasing for Unity is installed.   
2. Follow the [integration guide](https://en.docsv2.balancy.dev) to connect purchase events to your game logic.  

## Purchase recovery

Nutaku takes the payment outside of the game (a browser page on Android / iOS, a popup on WebGL). If the app is
killed while that page is open and the player pays afterwards, the gold is charged but the game never sees the
result. The module handles this on its own:

1. Before the Nutaku page opens, the purchase (offer / shop slot / store item and the Nutaku payment id) is written
   to `balancy_pending_purchases.json` in `Application.persistentDataPath` (the `PendingPurchaseManager` of the
   purchasing module).
2. On the next launch, once Balancy is synced and the payment system is initialized, `BalancyPaymentManager` calls
   `ProcessPendingPurchases()`: the module lists the purchases the server still holds as pending
   (`Balancy.API.NutakuPendingPurchases`), completes each of them, validates and grants the product, and removes
   the local record. The same pass runs when the app regains focus during a browser payment and when the Nutaku
   deep link arrives on a cold start.
3. The callback passed to `Balancy.API.InitPurchase*` in the killed session is gone, so grant the reward from
   `Balancy.Callbacks.OnHardPurchasedStoreItem / OnHardPurchasedOffer / OnHardPurchasedShopSlot / OnHardPurchasedOfferGroup`
   (they fire for recovered purchases too). `NutakuPurchaseSystem.PurchaseRecovered(productId, paymentId)` is
   raised afterwards if you want to show a message.
4. If a pending purchase has no local record (reinstall, another device), the module grants the store item that
   sells the product when there is exactly one. Set `NutakuPurchaseSystem.ResolveUnclaimedPurchase` to pick the
   `Balancy.Actions.BalancyProductInfo` yourself (return `null` to leave the purchase for later).

Requires Balancy Purchasing 1.5.8 and a Balancy SDK with `Balancy.API.NutakuPendingPurchases` (native libraries
rebuilt after 1.9.10).

## Documentation

For detailed setup instructions and examples, see:  
- [Balancy Docs](https://en.docsv2.balancy.dev)  
- [Release Notes](https://en.docsv2.balancy.dev/release_notes/)  

## Support

If you encounter issues or have questions:  
- [GitHub Issues](https://github.com/balancy-liveops/plugin_cpp_unity_nutaku/issues)  
- Email: [contact@balancy.co](mailto:contact@balancy.co)  
- [Discord Community](https://discord.gg/balancy)  

## License

This package is licensed under the MIT License – see the LICENSE file for details.
