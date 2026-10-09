import AppKit
import StoreKit

private typealias StoreCallback = @convention(c) (Int64, UnsafePointer<CChar>?) -> Void

@MainActor
private final class StoreBridge {
    static let shared = StoreBridge()
    var productId = ""
    var callback: StoreCallback?
    var listener: Task<Void, Never>?
    var requests: [Int64: Task<Void, Never>] = [:]

    func send(_ id: Int64, _ payload: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: payload),
              let text = String(data: data, encoding: .utf8) else { return }
        text.withCString { callback?(id, $0) }
    }

    func start(_ id: String, _ completion: @escaping StoreCallback) {
        stop()
        productId = id
        callback = completion
        listener = Task {
            for await update in Transaction.updates {
                if Task.isCancelled { break }
                switch update {
                case .verified(let transaction) where transaction.productID == productId:
                    send(0, [:])
                    await transaction.finish()
                case .unverified(let transaction, _) where transaction.productID == productId:
                    send(0, [:])
                default: break
                }
            }
        }
    }

    func product() async throws -> Product {
        guard let product = try await Product.products(for: [productId]).first,
              product.type == .autoRenewable, let subscription = product.subscription,
              subscription.subscriptionPeriod.unit == .year,
              subscription.subscriptionPeriod.value == 1 else {
            throw NSError(domain: "StoreKitBridge", code: 1, userInfo: [NSLocalizedDescriptionKey: "年度订阅商品未配置或不可用。"])
        }
        return product
    }

    func check() async throws -> [String: Any] {
        var latest: Transaction?
        var unverified = false
        for await result in Transaction.currentEntitlements {
            switch result {
            case .verified(let transaction):
                guard transaction.productID == productId, transaction.productType == .autoRenewable,
                      transaction.revocationDate == nil, !transaction.isUpgraded,
                      let expiry = transaction.expirationDate, expiry > Date() else { continue }
                if latest == nil || expiry > latest!.expirationDate! { latest = transaction }
            case .unverified(let transaction, _):
                if transaction.productID == productId { unverified = true }
            }
        }
        if let transaction = latest {
            // The introductory offer configured for this sole product is a free week.
            return ["state": transaction.offerType == .introductory ? "trial" : "subscribed",
                    "expiresAt": Int64(transaction.expirationDate!.timeIntervalSince1970 * 1000)]
        }
        if unverified { throw NSError(domain: "StoreKitBridge", code: 2, userInfo: [NSLocalizedDescriptionKey: "订阅交易未通过 Apple 验证。"]) }
        return ["state": "unsubscribed"]
    }

    func perform(_ operation: String) async throws -> [String: Any] {
        switch operation {
        case "check": return try await check()
        case "product":
            let item = try await product()
            let subscription = item.subscription!
            let offer = subscription.introductoryOffer
            let days: Int
            if let offer, offer.paymentMode == .freeTrial {
                switch offer.period.unit {
                case .day: days = offer.period.value * offer.periodCount
                case .week: days = offer.period.value * offer.periodCount * 7
                default: days = 0
                }
            } else { days = 0 }
            // Fail closed for an accidentally configured offer instead of promising a different trial.
            if offer != nil && days != 7 {
                throw NSError(domain: "StoreKitBridge", code: 3, userInfo: [NSLocalizedDescriptionKey: "请在 App Store Connect 配置 7 天免费试用。"])
            }
            let eligible = await subscription.isEligibleForIntroOffer
            return ["price": item.displayPrice, "trialEligible": days == 7 && eligible, "trialDays": days]
        case "purchase":
            let item = try await product()
            switch try await item.purchase() {
            case .success(let result):
                guard case .verified(let transaction) = result, transaction.productID == productId else {
                    throw NSError(domain: "StoreKitBridge", code: 2)
                }
                await transaction.finish()
                return ["outcome": "purchased"]
            case .userCancelled: return ["outcome": "cancelled"]
            case .pending: return ["outcome": "pending"]
            @unknown default: throw NSError(domain: "StoreKitBridge", code: 4)
            }
        case "restore":
            // Never called by a background entitlement check.
            try await AppStore.sync()
            return [:]
        case "manage":
            guard NSWorkspace.shared.open(URL(string: "https://apps.apple.com/account/subscriptions")!) else {
                throw NSError(domain: "StoreKitBridge", code: 5)
            }
            return [:]
        default: throw NSError(domain: "StoreKitBridge", code: 6)
        }
    }

    func request(_ id: Int64, _ operation: String) {
        requests[id] = Task {
            defer { requests[id] = nil }
            do {
                let result = try await perform(operation)
                if !Task.isCancelled { send(id, result) }
            } catch {
                if !Task.isCancelled { send(id, ["error": error.localizedDescription]) }
            }
        }
    }

    func stop() {
        listener?.cancel(); listener = nil
        for task in requests.values { task.cancel() }
        requests.removeAll()
        callback = nil
    }
}

@_cdecl("me_store_start")
public func storeStart(_ productId: UnsafePointer<CChar>, _ callback: @escaping @convention(c) (Int64, UnsafePointer<CChar>?) -> Void) {
    let id = String(cString: productId)
    DispatchQueue.main.async { StoreBridge.shared.start(id, callback) }
}

@_cdecl("me_store_request")
public func storeRequest(_ id: Int64, _ operation: UnsafePointer<CChar>) {
    let name = String(cString: operation)
    DispatchQueue.main.async { StoreBridge.shared.request(id, name) }
}

@_cdecl("me_store_cancel")
public func storeCancel(_ id: Int64) {
    DispatchQueue.main.async { StoreBridge.shared.requests[id]?.cancel() }
}

@_cdecl("me_store_stop")
public func storeStop() {
    DispatchQueue.main.async { StoreBridge.shared.stop() }
}
