/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using QuantConnect.Util;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using System.Collections.Generic;

namespace QuantConnect.Securities
{
    /// <summary>
    /// This class implements interface <see cref="ISecurityService"/> providing methods for creating new <see cref="Security"/>
    /// </summary>
    public class SecurityService : ISecurityService
    {
        private readonly CashBook _cashBook;
        private readonly MarketHoursDatabase _marketHoursDatabase;
        private readonly SymbolPropertiesDatabase _symbolPropertiesDatabase;
        private readonly IRegisteredSecurityDataTypesProvider _registeredTypes;
        private readonly ISecurityInitializerProvider _securityInitializerProvider;
        private readonly SecurityCacheProvider _cacheProvider;
        private readonly IPrimaryExchangeProvider _primaryExchangeProvider;
        private bool _isLiveMode;

        /// <summary>
        /// Creates a new instance of the SecurityService class
        /// </summary>
        public SecurityService(CashBook cashBook,
            MarketHoursDatabase marketHoursDatabase,
            SymbolPropertiesDatabase symbolPropertiesDatabase,
            ISecurityInitializerProvider securityInitializerProvider,
            IRegisteredSecurityDataTypesProvider registeredTypes,
            SecurityCacheProvider cacheProvider,
            IPrimaryExchangeProvider primaryExchangeProvider=null)
        {
            _cashBook = cashBook;
            _registeredTypes = registeredTypes;
            _marketHoursDatabase = marketHoursDatabase;
            _symbolPropertiesDatabase = symbolPropertiesDatabase;
            _securityInitializerProvider = securityInitializerProvider;
            _cacheProvider = cacheProvider;
            _primaryExchangeProvider = primaryExchangeProvider;
        }

        /// <summary>
        /// Creates a new security
        /// </summary>
        /// <remarks>Following the obsoletion of Security.Subscriptions,
        /// both overloads will be merged removing <see cref="SubscriptionDataConfig"/> arguments</remarks>
        public Security CreateSecurity(Symbol symbol,
            List<SubscriptionDataConfig> subscriptionDataConfigList,
            decimal leverage = 0,
            bool addToSymbolCache = true,
            Security underlying = null)
        {
            var configList = new SubscriptionDataConfigList(symbol);
            configList.AddRange(subscriptionDataConfigList);

            var exchangeHours = _marketHoursDatabase.GetEntry(symbol.ID.Market, symbol, symbol.ID.SecurityType).ExchangeHours;

            var defaultQuoteCurrency = _cashBook.AccountCurrency;
            if (symbol.ID.SecurityType == SecurityType.Forex)
            {
                defaultQuoteCurrency = symbol.Value.Substring(3);
            }

            if (symbol.ID.SecurityType == SecurityType.Crypto && !_symbolPropertiesDatabase.ContainsKey(symbol.ID.Market, symbol, symbol.ID.SecurityType))
            {
                throw new ArgumentException($"Symbol can't be found in the Symbol Properties Database: {symbol.Value}");
            }

            // For Futures Options that don't have a SPDB entry, the futures entry will be used instead.
            var symbolProperties = _symbolPropertiesDatabase.GetSymbolProperties(
                symbol.ID.Market,
                symbol,
                symbol.SecurityType,
                defaultQuoteCurrency);

            // add the symbol to our cache
            if (addToSymbolCache)
            {
                SymbolCache.Set(symbol.Value, symbol);
            }

            // verify the cash book is in a ready state
            var quoteCurrency = symbolProperties.QuoteCurrency;
            if (!_cashBook.TryGetValue(quoteCurrency, out var quoteCash))
            {
                // since we have none it's safe to say the conversion is zero
                quoteCash = _cashBook.Add(quoteCurrency, 0, 0);
            }

            Cash baseCash = null;
            // we skip cfd because we don't need to add the base cash
            if (symbol.SecurityType != SecurityType.Cfd && CurrencyPairUtil.TryDecomposeCurrencyPair(symbol, out var baseCurrencySymbol, out _))
            {
                if (!_cashBook.TryGetValue(baseCurrencySymbol, out baseCash))
                {
                    // since we have none it's safe to say the conversion is zero
                    baseCash = _cashBook.Add(baseCurrencySymbol, 0, 0);
                }
            }

            var cache = _cacheProvider.GetSecurityCache(symbol);

            Security security;
            switch (symbol.ID.SecurityType)
            {
                case SecurityType.Equity:
                    var primaryExchange =
                        _primaryExchangeProvider?.GetPrimaryExchange(symbol.ID) ??
                        Exchange.UNKNOWN;
                    security = new Equity.Equity(symbol, exchangeHours, quoteCash, symbolProperties, _cashBook, _registeredTypes, cache, primaryExchange);
                    break;

                case SecurityType.Option:
                    if (addToSymbolCache) SymbolCache.Set(symbol.Underlying.Value, symbol.Underlying);
                    security = new Option.Option(symbol, exchangeHours, quoteCash, new Option.OptionSymbolProperties(symbolProperties), _cashBook, _registeredTypes, cache, underlying);
                    break;

                case SecurityType.IndexOption:
                    if (addToSymbolCache) SymbolCache.Set(symbol.Underlying.Value, symbol.Underlying);
                    security = new IndexOption.IndexOption(symbol, exchangeHours, quoteCash, new IndexOption.IndexOptionSymbolProperties(symbolProperties), _cashBook, _registeredTypes, cache, underlying);
                    break;

                case SecurityType.FutureOption:
                    if (addToSymbolCache) SymbolCache.Set(symbol.Underlying.Value, symbol.Underlying);
                    var optionSymbolProperties = new Option.OptionSymbolProperties(symbolProperties);

                    // Future options exercised only gives us one contract back, rather than the
                    // 100x seen in equities.
                    optionSymbolProperties.SetContractUnitOfTrade(1);

                    security = new FutureOption.FutureOption(symbol, exchangeHours, quoteCash, optionSymbolProperties, _cashBook, _registeredTypes, cache, underlying);
                    break;

                case SecurityType.Future:
                    security = new Future.Future(symbol, exchangeHours, quoteCash, symbolProperties, _cashBook, _registeredTypes, cache, underlying);
                    break;

                case SecurityType.Forex:
                    security = new Forex.Forex(symbol, exchangeHours, quoteCash, baseCash, symbolProperties, _cashBook, _registeredTypes, cache);
                    break;

                case SecurityType.Cfd:
                    security = new Cfd.Cfd(symbol, exchangeHours, quoteCash, symbolProperties, _cashBook, _registeredTypes, cache);
                    break;

                case SecurityType.Index:
                    security = new Index.Index(symbol, exchangeHours, quoteCash, symbolProperties, _cashBook, _registeredTypes, cache);
                    break;

                case SecurityType.Crypto:
                    security = new Crypto.Crypto(symbol, exchangeHours, quoteCash, baseCash, symbolProperties, _cashBook, _registeredTypes, cache);
                    break;

                case SecurityType.CryptoFuture:
                    security = new CryptoFuture.CryptoFuture(symbol, exchangeHours, quoteCash, baseCash, symbolProperties, _cashBook, _registeredTypes, cache);
                    break;

                default:
                case SecurityType.Base:
                    security = new Security(symbol, exchangeHours, quoteCash, symbolProperties, _cashBook, _registeredTypes, cache);
                    break;
            }

            // if we're just creating this security and it only has an internal
            // feed, mark it as non-tradable since the user didn't request this data
            if (!configList.IsInternalFeed)
            {
                security.IsTradable = true;
            }

            security.AddData(configList);

            // invoke the security initializer
            _securityInitializerProvider.SecurityInitializer.Initialize(security);

            // if leverage was specified then apply to security after the initializer has run, parameters of this
            // method take precedence over the intializer
            if (leverage != Security.NullLeverage)
            {
                security.SetLeverage(leverage);
            }

            var isNotNormalized = configList.DataNormalizationMode() == DataNormalizationMode.Raw;

            // In live mode and non normalized data, equity assumes specific price variation model
            if ((_isLiveMode || isNotNormalized) && security.Type == SecurityType.Equity)
            {
                security.PriceVariationModel = new EquityPriceVariationModel();
            }

            return security;
        }

        /// <summary>
        /// Creates a new security
        /// </summary>
        /// <remarks>Following the obsoletion of Security.Subscriptions,
        /// both overloads will be merged removing <see cref="SubscriptionDataConfig"/> arguments</remarks>
        public Security CreateSecurity(Symbol symbol, SubscriptionDataConfig subscriptionDataConfig, decimal leverage = 0, bool addToSymbolCache = true, Security underlying = null)
        {
            return CreateSecurity(symbol, new List<SubscriptionDataConfig> { subscriptionDataConfig }, leverage, addToSymbolCache, underlying);
        }

        /// <summary>
        /// Set live mode state of the algorithm
        /// </summary>
        /// <param name="isLiveMode">True, live mode is enabled</param>
        public void SetLiveMode(bool isLiveMode)
        {
            _isLiveMode = isLiveMode;
        }
    }
}
