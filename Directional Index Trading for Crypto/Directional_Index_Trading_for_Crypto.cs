// Copyright QUANTOWER LLC. © 2017-2021. All rights reserved.

using System;
using System.Linq;
using System.Collections.Generic;
using TradingPlatform.BusinessLayer;

namespace Directional_Index_Trading_for_Crypto
{

    public class Directional_Index_Trading_for_Crypto : Strategy
    {

        #region Parameters

        [InputParameter("Micro Symbol", 10)]
        public Symbol symbol1;

        [InputParameter("Account", 20)]
        public Account account;

        [InputParameter("Limit Placement against previous candle low", 30)]
        public int x = 0;


        // Displays Input Parameter as input field (or checkbox if value type is bolean).
        [InputParameter("DMI Indicator Period", 0, 1, 999, 1, 0)]
        public int Period2 = 14;

        [InputParameter("Daily Loss Limit %", 0, 0, 100, 0.1, 1)]
        public double LimMax = 10;

        // Displays Input Parameter as dropdown list.
        [InputParameter("Type of Moving Average", 1, variants: new object[] {
            "Simple", MaMode.SMA,
            "Exponential", MaMode.EMA,
            "Modified", MaMode.SMMA,
            "Linear Weighted", MaMode.LWMA}
        )]
        public MaMode MAType = MaMode.SMA;

        [InputParameter("How does risk change on losing trade? ", variants: new object[] {
            "Risk Increases", -1,
            "Risk Decreases", 1
        })]
        public int RiskDir = 1;

        [InputParameter("Trading Direction", variants: new object[]{
            "Long & Short", 0,
            "Long Only", 1,
            "Short Only", 2
        })]
        public int TradeDir = 0;

        [InputParameter("Reward ratio", 0, 1, 100, .5, 1)]
        public double y = 3;

        [InputParameter("Hours of no trading after 9:30pm", minimum: 2, maximum: 22)]
        public int hoursWithoutTrading = 15;

        [InputParameter("Random Number for candle length? ")]
        public bool RandomTime = false;

        [InputParameter("Pause after 3 losses for 5 trades? ")]
        public bool LstreakPause = true;

        [InputParameter("Risk Percent per Trade", 0, 1, 100, .01, 2)]
        public double maxRisk = 2;

        [InputParameter("TIF", 5)]
        public TimeInForce time = TimeInForce.GTT;

        [InputParameter("Bars before cancel", 0, 1, 100)]
        public double MaxBars = 3;

        [InputParameter("ATR Indicator Dividor", 0, 0.01, 100)]
        public double z = 1;

        [InputParameter("Candle Time if not Random", 0, 1, 1000)]
        public int CandleTime = 156;

        [InputParameter("Choice of Lower close method", variants: new object[] {
            "Start Stop Loss X points above order price", 0,
            "Trailing Stop Loss", 1,
            "Breakeven with TP", 2,
            "Let Winners run", 3}
        )]
        public int ChoiceofStop = 3;

        [InputParameter("Points above Order Price")]
        public double PtsAbove = 0;
        /*[InputParameter("Minutes before opening new trade")]
        public double minutesbeforenew = 20;*/

        [InputParameter("Maintenance Margin")]
        public double maintm = 900;

        [InputParameter("Day Trade Margin")]
        public double daym = 50;

        [InputParameter("PnL per Tick", increment: 0.001, decimalPlaces: 3)]
        public double pricePerTick = 1;

        [InputParameter("Minimum Tick SL")]
        public int minTicks = 3;

        [InputParameter("Daily Loss Decrease/Stop", minimum: 0, maximum: 5)]
        public double LimDecrease;

        private HistoricalData _historicalLongData;
        private HistoricalData _historicalShortData;
        public Indicator longIndicatorDMI;
        public Indicator longIndicatorATR;
        public Indicator shortIndicatorDMI;
        public Indicator shortIndicatorATR;

        private TradingOperationResult _operationResult;
        private DateTime MarketClose;
        private DateTime MarketOpen;
        private DateTime LowMovementBegin;
        private DateTime LowMovementFinish;
        private bool aboveBE = false;
        private double totalPos = 0;
        private double sharpe;
        private double longP = 0;
        private double ShortP = 0;
        private bool hasSetTime = false;
        private int len;
        private double stdDev;
        public double OrderAmount;
        private double CurrOrderPrice;
        public double prevHigh;
        public double prevHigh2;
        public double prevLow;
        public double prevLow2;
        public double slPrice = 0;
        private int lossStreak = 0;
        public int numBars;
        public double startAccVal;
        private double orderPeriod;
        public double trailPrice;
        private double BEPtsAbove;
        private double CurrMaxRisk;
        private double lOrders;
        private double sOrders;
        private double PlOrders;
        private double PsOrders;
        private int DecreaseNum;
        private DateTime Tomorrow;
        private bool CircuitBreakerHit = false;
        private int Tradesat0Risk;
        private double avLong = 0;
        private double avShort = 0;
        private enum DMIFlat
        {
            Short,
            Long,
            Neither
        }

        #endregion Parameters

        public Order currentSL;
        public Directional_Index_Trading_for_Crypto()
            : base()
        {
            this.Name = "Buy/Sell on DMI for Crypto";
            this.Description = "This strategy buys and sells when DMI flattens";
        }

        protected override void OnCreated()
        {
            base.OnCreated();

            this.longIndicatorDMI = Core.Indicators.BuiltIn.DMI(Period2, MAType);
            this.longIndicatorATR = Core.Indicators.BuiltIn.ATR(Period2, MAType);
            this.shortIndicatorDMI = Core.Indicators.BuiltIn.DMI(Period2, MAType);
            this.shortIndicatorATR = Core.Indicators.BuiltIn.ATR(Period2, MAType);
        }

        protected override void OnRun()
        {
            
            CurrMaxRisk = maxRisk;
            if (symbol1 == null || account == null || symbol1.ConnectionId != account.ConnectionId)
            {
                Log("Incorrect input parameters... symbol1 or Account are not specified or they have diffent connectionID.", StrategyLoggingLevel.Error);
                return;
            }
            account.NettingType = NettingType.OnePosition;
            var vendorName = Core.Connections.Connected.FirstOrDefault(c => c.Id == symbol1.ConnectionId);
            var isLimitSupported = Core.GetOrderType(OrderType.Limit, symbol1.ConnectionId) != null;
            startAccVal = account.Balance;
            Log("Account Beginning Value: " + startAccVal);

            if (!isLimitSupported && vendorName != null)
            {
                Log($"The '{vendorName}' doesn't support '{OrderType.Limit}' order type.", StrategyLoggingLevel.Error);
                return;
            }
            _historicalLongData = symbol1.GetHistory(new HistoryAggregationHeikenAshi(HeikenAshiSource.Second, 159), HistoryType.Last, DateTime.Now.AddMinutes(-42));
            _historicalLongData.AddIndicator(longIndicatorDMI);
            _historicalLongData.AddIndicator(longIndicatorATR);

            _historicalShortData = symbol1.GetHistory(new HistoryAggregationHeikenAshi(HeikenAshiSource.Second, 188), HistoryType.Last, DateTime.Now.AddMinutes(-50));
            _historicalShortData.AddIndicator(shortIndicatorDMI);
            _historicalShortData.AddIndicator(shortIndicatorATR);

            
            Core.TradeAdded += UpdateTime;
            Tomorrow = symbol1.LastDateTime.FromSelectedTimeZoneToUtc().AddDays(1).Date;
            Core.PositionRemoved += Core_PositionRemoved;
            _historicalLongData.NewHistoryItem += this.historicalData_NewHistoryItem;
            _historicalShortData.NewHistoryItem += _historicalShortData_NewHistoryItem;
            PtsAbove /= 40000;
            Log("Strategy has began running");
            
            /*var timer = new System.Threading.Timer((e) =>
            {
                UpdateTime();
            }, null, TimeSpan.Zero, TimeSpan.FromHours(8));*/
            Log(symbol1.TickSize.ToString());
        }
        private void _historicalShortData_NewHistoryItem(object sender, HistoryEventArgs e)
        {
            /*MarketOpen = new DateTime(symbol1.LastDateTime.FromSelectedTimeZoneToUtc().AddDays(1).Year, symbol1.LastDateTime.FromSelectedTimeZoneToUtc().AddDays(1).Month, symbol1.LastDateTime.FromSelectedTimeZoneToUtc().AddDays(1).Day, 1, 0, 0);
            MarketClose = MarketOpen.AddHours(1);*/
                

            if ((CircuitBreakerHit == true && LimMax <= maxRisk + 1) /*|| (symbol1.LastDateTime.FromSelectedTimeZoneToUtc().Date > Tomorrow && CircuitBreakerHit == false)*/)
            {
                Log("Resetting Daily Stop Loss");
                LimMax += DecreaseNum * LimDecrease;
                DecreaseNum = 0;
                CircuitBreakerHit = false;
                Tomorrow = symbol1.LastDateTime.FromSelectedTimeZoneToUtc().AddDays(1).Date;
            }
            var symbLast = symbol1.LastDateTime.FromSelectedTimeZoneToUtc();
            /*if (LowMovementFinish == null)
            {
                LowMovementBegin = new DateTime(symbLast.Year, symbLast.Month, symbLast.Day, 22, 45, 0);
                LowMovementFinish = LowMovementBegin.Add(new TimeSpan(1, 30, 0));
            }
            StrategyProcess(true);*/
        }

        private void Core_PositionRemoved(Position pos)
        {
            if (account.Balance <= ((100 - LimMax) / 100) * startAccVal)
            {
                Log("Account Value exceeds daily loss limit", StrategyLoggingLevel.Error);
                CircuitBreakerHit = true;

                LowMovementBegin = symbol1.LastDateTime.FromSelectedTimeZoneToUtc(); // Cuts off trading until next day
                startAccVal = account.Balance;
                LimMax -= LimDecrease;
                DecreaseNum++;
                return;
            }
            //MarketOpen = new DateTime(symbol1.LastDateTime.AddDays(1).Year, symbol1.LastDateTime.AddDays(1).Month, symbol1.LastDateTime.AddDays(1).Day, 1, 0, 0).ToUniversalTime();
           // MarketClose = new DateTime(symbol1.LastDateTime.AddDays(1).Year, symbol1.LastDateTime.AddDays(1).Month, symbol1.LastDateTime.AddDays(1).Day, 0, 0, 0).ToUniversalTime();
        }

        private void UpdateTime(Trade pos) // Will not work if given a set time, as LowMovementFinish will change to a later date.
        {
            var symbLast = symbol1.LastDateTime.FromSelectedTimeZoneToUtc();
            
            if (LstreakPause)
            {
                ChangeLStreak(pos);
            }
            CircuitBreakerHit = false;
            /*if (symbLast < LowMovementFinish)
                return;
            
            LowMovementBegin = new DateTime(symbLast.Year, symbLast.Month, symbLast.Day, 22, 45, 0);
            LowMovementFinish = LowMovementBegin.Add(new TimeSpan(1, 30, 0));
            Log("Low Movement Beginning @ " + LowMovementBegin + " and finishing @ " + LowMovementFinish);*/

        }


        private void historicalData_NewHistoryItem(object sender, HistoryEventArgs e)
        {
            numBars++;
            if (CircuitBreakerHit == true && LimMax <= maxRisk + 1)
            {
                Log("Resetting Daily Stop Loss");
                LimMax += DecreaseNum * LimDecrease;
                DecreaseNum = 0;
                CircuitBreakerHit = false;
                Tomorrow = symbol1.LastDateTime.FromSelectedTimeZoneToUtc().AddDays(1).Date;
            }
            if (Core.Positions.Length != 0 && symbol1.LastDateTime.FromSelectedTimeZoneToUtc() >= MarketClose.AddMinutes(-30))
            {
                foreach(Position pos in Core.Positions)
                {
                    pos.Close();
                }
                Log("All Positions closed before close");
            }
            StrategyProcess(false);

        }

        private void StrategyProcess(bool isShort)
        {
            if (Core.Instance.Positions.Length == 0 && Core.Orders.Length > 0)
            {
                if (numBars >= MaxBars)
                {
                    foreach (Order o in Core.Orders)
                    {
                        o.Cancel();
                    }
                    numBars = 0;
                }
            }
            else if (symbol1.LastDateTime.FromSelectedTimeZoneToUtc() >= LowMovementBegin && Core.Positions.Length != 0)
            {
                foreach (Position pos in Core.Positions)
                {
                    pos.Close();
                }
                Log("Positions closed as time is after chosen time.");
                return;
            }

            if (isDMIFlat() != DMIFlat.Neither)
            {
                prevHigh = Math.Round(((HistoryItemBar)_historicalShortData[1]).High *2, MidpointRounding.ToEven)/2;
                prevHigh2 = Math.Round(((HistoryItemBar)_historicalShortData[2]).High *2,MidpointRounding.ToEven)/2;
                prevLow = Math.Round(((HistoryItemBar)_historicalLongData[1]).Low * 2, MidpointRounding.ToEven) / 2;
                prevLow2 = Math.Round(((HistoryItemBar)_historicalLongData[2]).Low * 2,MidpointRounding.ToEven)/ 2;

                if (Core.Instance.Positions.Length != 0)
                {
                    foreach (Position pos in Core.Instance.Positions)
                    {
                        if(((isDMIFlat() == DMIFlat.Long && pos.Side == Side.Buy) || (isDMIFlat() == DMIFlat.Short && pos.Side == Side.Sell)) && symbol1.LastDateTime.FromSelectedTimeZoneToUtc().Subtract(pos.OpenTime.FromSelectedTimeZoneToUtc()).TotalSeconds > orderPeriod) {
                            TradingOperationResult result = Core.Instance.ClosePosition(pos, pos.Quantity);

                            if (result.Status == TradingOperationResultStatus.Success)
                            {
                                Log($"{result.Status}. Position was closed", StrategyLoggingLevel.Info);
                            }
                            foreach (Order o in Core.Orders)
                            {
                                o.Cancel();
                            }
                            Log("Orders cancelled");
                        }
                    }
                    //TimeAfterPos = symbol1.LastDateTime.AddMinutes(minutesbeforenew)

                }
                if(Core.Positions.Length != 0)
                {
                    return;
                }
                if(avShort >= avLong)
                {
                    if ((TradeDir == 0 || TradeDir == 2) && prevHigh2 > prevHigh && (((HistoryItemBar)_historicalShortData[1]).Close - ((HistoryItemBar)_historicalShortData[1]).Open) > 0 && (((HistoryItemBar)_historicalShortData[2]).Close - ((HistoryItemBar)_historicalShortData[2]).Open > 0))
                    {
                        if (isShort && !(symbol1.LastDateTime.FromSelectedTimeZoneToUtc() < LowMovementBegin && symbol1.LastDateTime.FromSelectedTimeZoneToUtc() > LowMovementFinish))
                        {
                            CircuitBreakerHit = false;
                            CreateLimitOrder(Side.Sell, false);
                        }

                    }
                    else if ((TradeDir == 0 || TradeDir == 1) && prevLow2 < prevLow && (((HistoryItemBar)_historicalLongData[1]).Close - ((HistoryItemBar)_historicalLongData[1]).Open) < 0 && (((HistoryItemBar)_historicalLongData[2]).Close - ((HistoryItemBar)_historicalLongData[2]).Open < 0))
                    {
                        if (!isShort && !(symbol1.LastDateTime.FromSelectedTimeZoneToUtc() < LowMovementBegin && symbol1.LastDateTime.FromSelectedTimeZoneToUtc() > LowMovementFinish)) // This can be removed once realtime streaming begins
                        {
                            CircuitBreakerHit = false;
                            CreateLimitOrder(Side.Buy, false);
                        }

                    }

                } else
                {
                    if ((TradeDir == 0 || TradeDir == 1) && prevLow2 < prevLow && (((HistoryItemBar)_historicalLongData[1]).Close - ((HistoryItemBar)_historicalLongData[1]).Open) < 0 && (((HistoryItemBar)_historicalLongData[2]).Close - ((HistoryItemBar)_historicalLongData[2]).Open < 0))
                    {
                        if (!isShort && !(symbol1.LastDateTime.FromSelectedTimeZoneToUtc() < LowMovementBegin && symbol1.LastDateTime.FromSelectedTimeZoneToUtc() > LowMovementFinish)) // This can be removed once realtime streaming begins
                        {
                            CircuitBreakerHit = false;
                            CreateLimitOrder(Side.Buy, false);
                        }
                    } else if ((TradeDir == 0 || TradeDir == 2) && prevHigh2 > prevHigh && (((HistoryItemBar)_historicalShortData[1]).Close - ((HistoryItemBar)_historicalShortData[1]).Open) > 0 && (((HistoryItemBar)_historicalShortData[2]).Close - ((HistoryItemBar)_historicalShortData[2]).Open > 0))
                    {
                        if (isShort && !(symbol1.LastDateTime.FromSelectedTimeZoneToUtc() < LowMovementBegin && symbol1.LastDateTime.FromSelectedTimeZoneToUtc() > LowMovementFinish))
                        {
                            CircuitBreakerHit = false;
                            CreateLimitOrder(Side.Sell, false);
                        }

                    }
                }
            }
        }

        public void CreateLimitOrder(Side side, bool isShort, double Amm = 0)
        {
            if (CircuitBreakerHit && symbol1.LastDateTime.FromSelectedTimeZoneToUtc() < MarketOpen)
            {
                Log("Circuit Breaker hit inside CreateLimitOrder. Time: " + symbol1.LastDateTime.FromSelectedTimeZoneToUtc().ToString());
                return;
            }
            try
            {
                if (symbol1.LastDateTime.FromSelectedTimeZoneToUtc() >= MarketClose.AddMinutes(-30) && symbol1.LastDateTime.FromSelectedTimeZoneToUtc() <= MarketOpen)
                {
                    Log("No position entered. Too close to market close.");
                    return;
                }
            } catch(Exception e)
            {
                Log(MarketClose + " , Error: " + e.ToString());
            }
            if (LstreakPause)
            {
                if (Tradesat0Risk > 3)
                {
                    CurrMaxRisk = maxRisk;
                    Tradesat0Risk = 0;
                    lossStreak = 0;
                }
                else if (lossStreak > 3 || (Tradesat0Risk <= 3 && Tradesat0Risk > 0))
                {

                    if (Tradesat0Risk == 0)
                    {
                        Tradesat0Risk++;
                    }
                    Tradesat0Risk++;
                    Log("Trades stopped. Not enough 0 risk trades", StrategyLoggingLevel.Error);
                    return;
                }
            }

            /* else if (symbol1.LastDateTime < TimeAfterPos)
            {
                Log("Too Early to open another trade");
                return;
            }*/
            if (_operationResult != null)
            {
                Log("Problem lies here");
                if (Core.GetPositionById(_operationResult.OrderId, symbol1.ConnectionId) != null)
                    return;

                var order = Core.Orders.FirstOrDefault(o => o.ConnectionId == symbol1.ConnectionId && o.Id == _operationResult.OrderId);
                if (order != null)
                {
                    order.Cancel();
                    Log("Order was canceled.", StrategyLoggingLevel.Trading);
                }
            }
            var sign = (side == Side.Buy) ? -1 : 1;

            double orderPrice;
            Log("Bid: " + symbol1.Bid + ", Ask: " + symbol1.Ask);
            Log("Percent Above/Below: " + PtsAbove);
            //orderPrice =  (side == Side.Buy) ? symbol1.Bid * (1 +  sign * PtsAbove) : symbol1.Ask * (1 + sign * PtsAbove);
            orderPrice = Math.Round(symbol1.Last * (1 + sign * PtsAbove)*2, MidpointRounding.ToEven)/2;
            //orderPrice = Math.Round(((side == Side.Buy ? ((HistoryItemBar)_historicalLongData[1]).Low : ((HistoryItemBar)_historicalLongData[1]).High) + sign * x) * symbol1.TickSize, MidpointRounding.ToEven)/symbol1.TickSize;


            /*var lowOrder = orderPrice - prevLow2 + (x * symbol1.TickSize);
            var highOrder = prevHigh2 - orderPrice + (x * symbol1.TickSize);*/

            var indRounded = Math.Round((longIndicatorATR.GetValue(1) / z) * (symbol1.TickSize >= 1 ? symbol1.TickSize : 1 / symbol1.TickSize), MidpointRounding.ToEven) / (symbol1.TickSize >= 1 ? symbol1.TickSize : 1 / symbol1.TickSize);
            slPrice =  indRounded;


            double Amount = 1;
            Amount = Math.Round(CurrMaxRisk * account.Balance / (100 * slPrice), 3);

            if (Amount == 0)
            {
                Log("Trading failed. 0 Amount size");
                Stop();
            }
            var StopL = SlTpHolder.CreateSL(slPrice, PriceMeasurement.Offset);
            var TakeP = SlTpHolder.CreateTP(y * slPrice, PriceMeasurement.Offset);
            if (Amount > 0)
            {
                _operationResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                {
                    Account = this.account,
                    Symbol = this.symbol1,
                    Side = side,
                    Price = orderPrice,
                    Quantity = Amount,
                    TimeInForce = TimeInForce.GTT,
                    ExpirationTime = DateTime.Now.AddMinutes(MaxBars * (side == Side.Buy ? _historicalLongData.Period.Duration.TotalMinutes : _historicalShortData.Period.Duration.TotalMinutes)),
                    StopLoss = StopL,
                    TakeProfit = TakeP,
                    OrderTypeId = OrderType.Limit
                });
            } else
            {
                Log("Amount Less than/equal to 0. Error! Amount Size: " + Amount, StrategyLoggingLevel.Error);
                Stop();
            }

            orderPeriod = (side == Side.Buy ?  _historicalLongData.Period.Duration.TotalSeconds : _historicalShortData.Period.Duration.TotalSeconds);

            trailPrice = Math.Round(orderPrice + sign * slPrice, MidpointRounding.ToEven);
            BEPtsAbove = orderPrice + PtsAbove * symbol1.Last;
            //PtsAbove = slPrice + 10 * symbol1.TickSize;
            numBars = 0;
            var formattedSide = string.Empty;
            if (side == Side.Buy)
            {
                formattedSide = "Long";
                Log("Long Buy");
                lOrders+= Amount;
            }
            else
            {
                formattedSide = "Short";
                Log("Short Sale");
                sOrders+= Amount;
            }
            if (_operationResult.Status == TradingOperationResultStatus.Success)
                Log($"{_operationResult.Status}. {formattedSide} order was placed @ {orderPrice}.", StrategyLoggingLevel.Trading);
            if (_operationResult.Status == TradingOperationResultStatus.Failure)
            {
                Log($"{_operationResult.Message}. {formattedSide} order failed to be placed @ {orderPrice}. Amount: " + Amount + ", SL: " + slPrice, StrategyLoggingLevel.Error);
            }
        }
        
        protected override void OnStop()
        {
            if (_historicalLongData == null)
                return;

            _historicalLongData.RemoveIndicator(longIndicatorDMI);
            _historicalLongData.RemoveIndicator(longIndicatorATR);

            _historicalLongData.NewHistoryItem -= this.historicalData_NewHistoryItem;
            foreach (Position pos in Core.Positions)
            {
                pos.Close(pos.Quantity);
            }
            foreach (Order order in Core.Orders)
            {
                order.Cancel();
            }
            Log("All Orders closed");
            Log("Account Final Balance: " + account.Balance);

        }
        protected override void OnRemove()
        {
            if (_historicalLongData != null)
                _historicalLongData.Dispose();
        }
        protected override List<StrategyMetric> OnGetMetrics()
        {
            List<StrategyMetric> result = base.OnGetMetrics();

            

            // An example of adding custom strategy metrics:
            result.Add("Random choice of seconds", orderPeriod.ToString());
            result.Add("Average Profit - Long", avLong.ToString());
            result.Add("Average Profit - Short", avShort.ToString());
            result.Add("Total Bitcoin Traded: ", (sOrders + lOrders).ToString());
            result.Add("Circuit Breaker hit: ", CircuitBreakerHit.ToString());
            return result;
        }
        private DMIFlat isDMIFlat()
        {
            if (Math.Round(shortIndicatorDMI.GetValue(1, 0), 4) == Math.Round(shortIndicatorDMI.GetValue(2, 0), 4) && Math.Round(shortIndicatorDMI.GetValue(1, 1), 4) == Math.Round(shortIndicatorDMI.GetValue(2, 1), 4)) { return DMIFlat.Short; }
            else if ((Math.Round(longIndicatorDMI.GetValue(1, 0), 4) == Math.Round(longIndicatorDMI.GetValue(2, 0), 4) && Math.Round(longIndicatorDMI.GetValue(1, 1), 4) == Math.Round(longIndicatorDMI.GetValue(2, 1), 4))) { return DMIFlat.Long; }
            return DMIFlat.Neither;
        }
        public void ChangeLStreak(Trade pos)
        {
            if (pos.PositionImpactType == PositionImpactType.Close)
            {
                if (pos.GrossPnl.Value <= 0)
                {
                    lossStreak++;

                    CurrMaxRisk -= RiskDir * 0.5;
                    if (pos.Side == Side.Buy)
                    {
                        PsOrders += pos.GrossPnl.Value;
                    }
                    else
                    {
                        PlOrders += pos.GrossPnl.Value;
                    }
                    avLong = (PlOrders/lOrders == double.PositiveInfinity | PlOrders/lOrders == double.NaN ? 0 : Math.Round(PlOrders/lOrders, 2));
                    avShort =  PsOrders/sOrders == double.PositiveInfinity | PsOrders / sOrders == double.NaN ? 0 : Math.Round(PsOrders / sOrders, 2);
                    Log("Max risk changed: " + CurrMaxRisk);
                }
                else
                {

                    if (pos.Side == Side.Buy)
                    {
                        PsOrders += pos.GrossPnl.Value;
                    }
                    else
                    {
                        PlOrders += pos.GrossPnl.Value;
                    }
                    if (startAccVal < account.Balance)
                    {
                        startAccVal = account.Balance;
                    }
                    avLong = (PlOrders / lOrders == double.PositiveInfinity | PlOrders / lOrders == double.NaN ? 0 : Math.Round(PlOrders / lOrders, 2));
                    avShort = PsOrders / sOrders == double.PositiveInfinity | PsOrders / sOrders == double.NaN ? 0 : Math.Round(PsOrders / sOrders, 2);
                    lossStreak = 0;
                    CurrMaxRisk = maxRisk;
                }
            }
            
        }


    }

}