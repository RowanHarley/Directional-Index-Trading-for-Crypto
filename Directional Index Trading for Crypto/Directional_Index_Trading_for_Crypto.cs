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

        [InputParameter("Mini Symbol")]
        public Symbol symbol2;

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

        [InputParameter("PnL per Tick")]
        public double pricePerTick = 0.5;
        [InputParameter("Minimum Tick SL")]
        public int minTicks = 3;

        [InputParameter("Daily Loss Decrease/Stop", minimum: 0, maximum: 5)]
        public double LimDecrease;

        private HistoricalData _historicalSecData;
        public Indicator indicatorDMI;
        public Indicator indicatorATR;


        private TradingOperationResult _operationResult;
        private DateTime MarketClose = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, 21, 0, 0);
        private DateTime MarketOpen = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, 22, 0, 0);
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
        public double prevOpen;
        public double prevOpen2;
        public double prevClose;
        public double prevClose2;
        public double prevHigh;
        public double prevHigh2;
        public double prevLow;
        public double prevLow2;
        public double slPrice = 0;
        private int lossStreak = 0;
        public int numBars;
        public double startAccVal;
        private int rndNum;
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
        private Order orderPeriod;

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

            this.indicatorDMI = Core.Indicators.BuiltIn.DMI(Period2, MAType);
            this.indicatorATR = Core.Indicators.BuiltIn.ATR(Period2, MAType);
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
            Random rnd = new Random();
            rndNum = RandomTime == true ? rnd.Next(1, 250) : CandleTime;
            _historicalSecData = symbol1.GetHistory(new HistoryAggregationHeikenAshi(HeikenAshiSource.Second, rndNum), HistoryType.Last, DateTime.Now.AddMinutes(-30));

            _historicalSecData.AddIndicator(indicatorDMI);
            _historicalSecData.AddIndicator(indicatorATR);

            Core.TradeAdded += UpdateTime;
            trailPrice = slPrice; //Math.Ceiling(indicatorATR.GetValue(1)/3);
            Tomorrow = symbol1.LastDateTime.FromSelectedTimeZoneToUtc().AddDays(1).Date;
            Core.PositionRemoved += Core_PositionRemoved;
            symbol1.NewDayBar += Symbol1_NewDayBar;
            _historicalSecData.NewHistoryItem += this.historicalData_NewHistoryItem;
            Log("Strategy has began running");

            /*var timer = new System.Threading.Timer((e) =>
            {
                UpdateTime();
            }, null, TimeSpan.Zero, TimeSpan.FromHours(8));*/
            Log(symbol1.TickSize.ToString());
        }

        private void Symbol1_NewDayBar(Symbol symbol, DayBar dayBar)
        {
            throw new NotImplementedException();
        }

        private void Core_PositionRemoved(Position pos)
        {
            if (account.Balance <= ((100 - LimMax) / 100) * startAccVal)
            {
                Log("Account Value exceeds daily loss limit", StrategyLoggingLevel.Error);
                CircuitBreakerHit = true;

                LowMovementFinish = symbol1.LastDateTime.FromSelectedTimeZoneToUtc().AddHours(6); // Cuts off trading until next day
                startAccVal = account.Balance;
                LimMax -= LimDecrease;
                DecreaseNum++;
                return;
            }

        }

        private void UpdateTime(Trade pos) // Will not work if given a set time, as LowMovementFinish will change to a later date.
        {
            if (LstreakPause)
            {
                ChangeLStreak(pos);
            }

        }


        private void historicalData_NewHistoryItem(object sender, HistoryEventArgs e)
        {

            numBars++;
            /*if (LimMax <= 0)
            {
                Stop();
            }*/
            if ((CircuitBreakerHit == true && LimMax <= maxRisk + 1) /*|| (symbol1.LastDateTime.FromSelectedTimeZoneToUtc().Date > Tomorrow && CircuitBreakerHit == false)*/)
            {
                LimMax += DecreaseNum * LimDecrease;
                DecreaseNum = 0;
                CircuitBreakerHit = false;
                Tomorrow = symbol1.LastDateTime.FromSelectedTimeZoneToUtc().AddDays(symbol1.LastDateTime.FromSelectedTimeZoneToUtc().DayOfWeek == DayOfWeek.Friday ? 2 : 1).Date;
            }
            StrategyProcess();

        }

        private void StrategyProcess()
        {
            if(account.Balance <= 0)
            {
                Stop();
            }

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
            else if (CircuitBreakerHit == true && Core.Positions.Length != 0)
            {
                foreach (Position pos in Core.Positions)
                {
                    pos.Close();
                }
                Log("Positions closed as time is after chosen time.");
            }
            if (Math.Round(indicatorDMI.GetValue(1, 0), 4) == Math.Round(indicatorDMI.GetValue(2, 0), 4) && Math.Round(indicatorDMI.GetValue(1, 1), 4) == Math.Round(indicatorDMI.GetValue(2, 1), 4))
            {
                prevHigh = ((HistoryItemBar)_historicalSecData[1]).High;
                prevHigh2 = ((HistoryItemBar)_historicalSecData[2]).High;
                prevLow = ((HistoryItemBar)_historicalSecData[1]).Low;
                prevLow2 = ((HistoryItemBar)_historicalSecData[2]).Low;
                prevClose = ((HistoryItemBar)_historicalSecData[1]).Close;
                prevClose2 = ((HistoryItemBar)_historicalSecData[2]).Close;
                prevOpen = ((HistoryItemBar)_historicalSecData[1]).Open;
                prevOpen2 = ((HistoryItemBar)_historicalSecData[2]).Open;

                if (Core.Instance.Positions.Length != 0)
                {
                    var pos = Core.Positions[0];
                    if (symbol1.LastDateTime.FromSelectedTimeZoneToUtc().Subtract(pos.OpenTime.FromSelectedTimeZoneToUtc()).TotalSeconds > rndNum)
                    {
                        TradingOperationResult result = Core.Instance.ClosePosition(pos, pos.Quantity);

                        if (result.Status != TradingOperationResultStatus.Success)
                        {
                            Log($"{result.Status}. Position was closed", StrategyLoggingLevel.Error);
                        }
                        foreach (Order o in Core.Orders)
                        {
                            o.Cancel();
                        }
                    }
                    //TimeAfterPos = symbol1.LastDateTime.AddMinutes(minutesbeforenew)

                }

                if ((TradeDir == 0 || TradeDir == 2) && prevHigh2 > prevHigh && (((HistoryItemBar)_historicalSecData[1]).Close - ((HistoryItemBar)_historicalSecData[1]).Open) > 0 && (((HistoryItemBar)_historicalSecData[2]).Close - ((HistoryItemBar)_historicalSecData[2]).Open > 0))
                {
                    if (CircuitBreakerHit == false || symbol1.LastDateTime.FromSelectedTimeZoneToUtc() > LowMovementFinish)
                    {
                        CircuitBreakerHit = false;
                        CreateLimitOrder(Side.Sell);
                    }

                }
                else if ((TradeDir == 0 || TradeDir == 1) && prevLow2 < prevLow && (((HistoryItemBar)_historicalSecData[1]).Close - ((HistoryItemBar)_historicalSecData[1]).Open) < 0 && (((HistoryItemBar)_historicalSecData[2]).Close - ((HistoryItemBar)_historicalSecData[2]).Open < 0))
                {
                    if (CircuitBreakerHit == false || symbol1.LastDateTime.FromSelectedTimeZoneToUtc() > LowMovementFinish)
                    {
                        CircuitBreakerHit = false;
                        CreateLimitOrder(Side.Buy);
                    }

                } /*else if (prevHigh == prevHigh2 && (((HistoryItemBar)_historicalSecData[1]).Close - ((HistoryItemBar)_historicalSecData[1]).Open) > 0 && (((HistoryItemBar)_historicalSecData[2]).Close - ((HistoryItemBar)_historicalSecData[2]).Open > 0))
                {
                    CreateLimitOrder(Side.Buy, false);
                } else if (prevLow2 == prevLow && (((HistoryItemBar)_historicalSecData[1]).Close - ((HistoryItemBar)_historicalSecData[1]).Open) < 0 && (((HistoryItemBar)_historicalSecData[2]).Close - ((HistoryItemBar)_historicalSecData[2]).Open < 0))
                {
                    CreateLimitOrder(Side.Sell, false);
                }*/
            }
        }

        public void CreateLimitOrder(Side side)
        {
            if (CircuitBreakerHit && symbol1.LastDateTime.FromSelectedTimeZoneToUtc() < LowMovementFinish)
            {
                Log("Circuit Breaker hit inside CreateLimitOrder. Time: " + symbol1.LastDateTime.FromSelectedTimeZoneToUtc().ToString());
                return;
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
                    return;
                }
            }

            if (CircuitBreakerHit && symbol1.LastDateTime.FromSelectedTimeZoneToUtc() < LowMovementFinish)
            {
                return;
            }

            /* else if (symbol1.LastDateTime < TimeAfterPos)
            {
                Log("Too Early to open another trade");
                return;
            }*/
            if (_operationResult != null)
            {
                if (Core.GetPositionById(_operationResult.OrderId, symbol1.ConnectionId) != null)
                    return;

                var order = Core.Orders.FirstOrDefault(o => o.ConnectionId == symbol1.ConnectionId && o.Id == _operationResult.OrderId);
                if (order != null)
                {
                    order.Cancel();
                }
            }
            var sign = (side == Side.Buy) ? -1 : 1;

            double orderPrice = symbol1.Last + sign * PtsAbove;
            //orderPrice = Math.Round(((side == Side.Buy ? ((HistoryItemBar)_historicalSecData[1]).Low : ((HistoryItemBar)_historicalSecData[1]).High) + sign * x) * symbol1.TickSize, MidpointRounding.ToEven)/symbol1.TickSize;


            // var lowOrder = orderPrice - prevLow2 + (x * symbol1.TickSize);
            // var highOrder = prevHigh2 - orderPrice + (x * symbol1.TickSize);

            var indRounded = Math.Round((indicatorATR.GetValue(1) / z), 3);
            slPrice = indRounded;

            double Amount = Math.Round(CurrMaxRisk * account.Balance / (100 * slPrice), 3);


            if (Amount == 0)
            {
                Log("Trading failed. 0 Amount size");
                Stop();
            }
            var StopL = SlTpHolder.CreateSL(slPrice * 100, PriceMeasurement.Offset);
            var TakeP = SlTpHolder.CreateTP(y * slPrice * 100, PriceMeasurement.Offset);

            _operationResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Account = this.account,
                Symbol = this.symbol1,
                Side = side,
                Price = orderPrice,
                Quantity = Amount,
                TimeInForce = TimeInForce.GTT,
                ExpirationTime = DateTime.Now.AddMinutes(MaxBars * _historicalSecData.Period.Duration.TotalMinutes),
                StopLoss = StopL,
                TakeProfit = TakeP,
                OrderTypeId = OrderType.Limit
            });



            trailPrice = Math.Round(orderPrice + sign * slPrice, MidpointRounding.ToEven);
            BEPtsAbove = orderPrice + PtsAbove;
            //PtsAbove = slPrice + 10 * symbol1.TickSize;
            numBars = 0;
            var formattedSide = string.Empty;
            if (side == Side.Buy)
            {
                formattedSide = "Long";
                lOrders+= Amount;
            }
            else
            {
                formattedSide = "Short";
                sOrders+= Amount;
            }
            if (_operationResult.Status == TradingOperationResultStatus.Failure)
            {
                Log($"{_operationResult.Message}. {formattedSide} order failed to be placed @ {orderPrice}. Amount: " + Amount + ", SL: " + slPrice, StrategyLoggingLevel.Error);
            }
        }
        private void OnQuote(Symbol instrument, Quote quote)
        {
            if (ChoiceofStop == 3)
            {
                return;
            }
            if (Core.Positions.Length == 0)
            {
                return;
            }
            if (symbol1.LastDateTime.FromSelectedTimeZoneToUtc() >= new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, 8, 30, 0))
            {
                foreach (Position pos in Core.Positions)
                {
                    pos.Close();
                }
            }
            if (ChoiceofStop == 2 && symbol1.Last >= BEPtsAbove && aboveBE == false)
            {
                trailPrice = Core.Positions[0].OpenPrice + 3;
                aboveBE = true;
            }
            if (Core.Positions.Length > 1)
            {
                Log("Current Positions greater than 1! Count: " + Core.Positions.Length, StrategyLoggingLevel.Error);
                Stop();
            }
            foreach (Position pos in Core.Positions)
            {
                var sign = (pos.Side == Side.Buy) ? -1 : 1;
                if ((((ChoiceofStop == 0 && symbol1.Last >= CurrOrderPrice + PtsAbove) || ChoiceofStop == 1) && ((pos.Side == Side.Buy && symbol1.Last <= trailPrice) || (pos.Side == Side.Sell && symbol1.Last >= trailPrice))) || (ChoiceofStop == 2 && aboveBE == true && symbol1.Last <= trailPrice))
                {
                    TradingOperationResult result = Core.Instance.ClosePosition(new ClosePositionRequestParameters()
                    {
                        Position = pos,
                        CloseQuantity = pos.Quantity
                    });


                    if (result.Status == TradingOperationResultStatus.Success)
                    {
                        Log($"{result.Status}. Position was closed. See Line 351. Positions Left: " + Core.Positions.Length, StrategyLoggingLevel.Trading);

                    }
                    else
                        Log($"{result.Message}. Position could not be closed. See Line 353", StrategyLoggingLevel.Error);

                    //TimeAfterPos = symbol1.LastDateTime.AddMinutes(minutesbeforenew);

                }
                var trail = Math.Round((symbol1.Last + sign * (indicatorATR.GetValue()) / z) * symbol1.TickSize, MidpointRounding.ToEven) / symbol1.TickSize;

                if ((pos.Side == Side.Buy && trail > trailPrice) || (pos.Side == Side.Sell && trail < trailPrice))
                {
                    trailPrice = trail;
                }

            }
            foreach (Order o in Core.Orders)
            {
                o.Cancel();
            }


        }
        protected override void OnStop()
        {
            if (_historicalSecData == null)
                return;

            _historicalSecData.RemoveIndicator(indicatorDMI);
            _historicalSecData.RemoveIndicator(indicatorATR);

            _historicalSecData.NewHistoryItem -= this.historicalData_NewHistoryItem;
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
            if (_historicalSecData != null)
                _historicalSecData.Dispose();
        }
        protected override List<StrategyMetric> OnGetMetrics()
        {
            List<StrategyMetric> result = base.OnGetMetrics();


            // An example of adding custom strategy metrics:
            result.Add("Random choice of seconds", rndNum.ToString());
            result.Add("Average Profit - Long", Math.Round(PlOrders / lOrders, 2).ToString());
            result.Add("Average Profit - Short", Math.Round(PsOrders / sOrders, 2).ToString());
            result.Add("Circuit Breaker hit: ", CircuitBreakerHit.ToString());
            result.Add("Total Bitcoin Traded: ", (lOrders + sOrders).ToString());
            result.Add("Acc. Balance: ", account.Balance.ToString());
            return result;
        }
        public void ChangeLStreak(Trade pos)
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
                lossStreak = 0;
                CurrMaxRisk = maxRisk;
            }
        }



    }

}