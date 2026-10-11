using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class MNQ_IchimokuBbCompositebacktest : Strategy
    {
        private const string FamilyIchimokuTouch = "ICHTouch";
        private const string FamilyIchimokuBreakout = "ICHBreakout";
        private const string FamilyBbCross = "BBCross";
        private const string FamilyBbReversion = "BBReversion";
        private const string FamilyBigTradeFollower = "BTOneTickBigTrade";
        private const int MarketOpenLongOverrideStartMinutes = 5;
        private const int MarketOpenLongOverrideEndMinutes = 30;

        private Bollinger bollinger;
        private MACD macd;
        private EMA bigTradeEmaFilter;
        private ATR bigTradeAtrFilter;
        private ADX bigTradeAdxFilter;

        private int oneHourBias;
        private int signalSequence;
        private int lastIchimokuTouchEntryBar = -1;
        private int closedSignalBarsAgo = 1;
        private DateTime lastClosedSignalProcessedTime = DateTime.MinValue;
        private DateTime lastOpenProtectionExitDate = DateTime.MinValue;
        private DateTime lastTradeEndExitDate = DateTime.MinValue;

        private readonly List<ActiveTrade> activeTrades = new List<ActiveTrade>();
        private readonly Dictionary<string, StrategySummary> performanceSummaries = new Dictionary<string, StrategySummary>();
        private string tradeExportPath = string.Empty;
        private string summaryExportPath = string.Empty;
        private bool performanceExportReady;

        private int bbCrossDeferredDirection;
        private int bbCrossDeferredStartBar = -1;
        private int bbCrossLossReentryDirection;
        private int bbCrossLossReentryUntilBar = -1;
        private DateTime bbCrossDoubleLossBlockedUntil = DateTime.MinValue;

        private DateTime bbReversionCloudCooldownUntil = DateTime.MinValue;
        private int bbReversionBypassCloudCooldownUntilBar = -1;
        private int bbReversionLongFirstConditionBar = -1;
        private int bbReversionShortFirstConditionBar = -1;
        private int bbReversionStopReentryDirection;
        private int bbReversionStopReentryBar = -1;
        private int bbReversionMarketOpenLossPendingBar = -1;
        private int bbReversionMarketOpenLossNoEntryUntilBar = -1;

        private bool bbPairLongBelowCloudLossPyramidBlocked;
        private int bbPairDailyConsecutiveLosses;
        private DateTime bbPairDailyLossDate = DateTime.MinValue;
        private DateTime bbPairEntryBlockedUntil = DateTime.MinValue;

        private double bigTradeBestBid = double.NaN;
        private double bigTradeBestAsk = double.NaN;
        private double bigTradePreviousTradePrice = double.NaN;
        private bool? bigTradePreviousTickRuleSide;
        private double bigTradeSessionCumPV;
        private double bigTradeSessionCumVol;
        private double bigTradeSessionVwap;
        private double bigTradeLastPrimaryBarVolume;
        private int bigTradeLastVwapPrimaryBar = -1;
        private int bigTradeLastSessionResetBar = -1;
        private int bigTradeLastSignalPrimaryBar = -1;
        private int bigTradeLosingTradesThisSession;
        private bool bigTradeDailyLossLimitReached;

        private class ActiveTrade
        {
            public string Family;
            public string Leg1Signal;
            public string Leg2Signal;
            public DateTime EntryTime;
            public int Direction;
            public bool IsDoubleReentry;
            public bool StopRegistered;
            public bool TimeExitSubmitted;
            public bool ProfitTargetFilled;
            public bool BbReversionCloudExitEligible;
            public bool BbPairLongEntryBelowCloud;
            public bool BigTradeRunnerStopMoved;
            public int BigTradeInitialStopTicks;
            public int OpenLeg1Quantity;
            public int OpenLeg2Quantity;
            public int EntryQuantity;
            public int ExitQuantity;
            public int RequestedQuantity;
            public double EntryValue;
            public double ExitValue;
            public double GrossPoints;
            public double GrossPnL;
            public string SignalId;
            public string LastExitReason;

            public bool IsClosed()
            {
                return OpenLeg1Quantity <= 0 && OpenLeg2Quantity <= 0;
            }

            public double AverageEntryPrice()
            {
                return EntryQuantity <= 0 ? 0 : EntryValue / EntryQuantity;
            }

            public double AverageExitPrice()
            {
                return ExitQuantity <= 0 ? 0 : ExitValue / ExitQuantity;
            }
        }

        private class StrategySummary
        {
            public int Trades;
            public int Winners;
            public int Losers;
            public int Breakeven;
            public int Quantity;
            public double Notional;
            public double GrossProfit;
            public double GrossLoss;
            public double NetProfit;
            public double NetPoints;
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "MNQ_IchimokuBbCompositebacktest";
                Description = "Backtest version of MNQ Ichimoku/BB composite strategy.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 20;
                EntryHandling = EntryHandling.AllEntries;
                DefaultQuantity = 1;
                IsExitOnSessionCloseStrategy = false;
                ExitOnSessionCloseSeconds = 30;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelCloseIgnoreRejects;
                IsInstantiatedOnEachOptimizationIteration = false;
                BarsRequiredToTrade = 120;
                Slippage = 1;

                EnableIchimokuTouch = true;
                EnableIchimokuBreakout = true;
                EnableBbCross = true;
                EnableBbReversion = true;
                EnableBigTradeFollower = true;

                MarketOpenTime = 93000;
                UseMarketOpenProtection = true;
                TradeStartTime = 80000;
                TradeEndTime = 120000;
                ExitAtTradeEnd = true;

                BbBaseQuantity = 2;
                IchimokuQuantityMultiplier = 2;
                DoubleReentryMultiplier = 2;

                MacdFast = 12;
                MacdSlow = 26;
                MacdSmooth = 9;
                BollingerLength = 20;
                BollingerStdDev = 2.0;

                IchimokuTenkanPeriod = 9;
                IchimokuKijunPeriod = 26;
                IchimokuSenkouBPeriod = 52;
                IchimokuDisplacement = 26;
                UseDisplacedIchimokuCloud = true;

                TouchLookbackBars = 13;
                IchimokuTouchBufferTicks = 0;
                BreakoutMacdLookbackBars = 15;
                RequireBreakoutBiasDirection = false;

                VolatilityLookbackBars = 10;
                IchimokuStopRangeMultiplier = 2.0;
                BbStopRangeMultiplier = 1.25;
                TimeExitMinutes = 15;

                BbCrossCloudLookbackBars = 15;
                BbCrossPriorReversionBars = 5;
                BbCrossDeferredMinBars = 5;
                BbCrossDeferredMaxBars = 15;
                BbCrossLossReentryBars = 10;
                BbCrossDoubleLossBlockMinutes = 15;
                BbCrossMiddleCloseTolerancePoints = 5.0;
                BbCrossStopRangeMultiplier = 1.25;

                BbReversionRetestMinBars = 3;
                BbReversionRetestMaxBars = 10;
                BbReversionCooldownMinutes = 10;
                BbReversionLossBypassBars = 10;
                BbPairDailyLossLimit = 3;
                BbPairDailyLossBlockMinutes = 45;

                BigTradeVolume = 90;
                BigTradeQuantity = 4;
                BigTradeScaleOutQuantity = 2;
                BigTradeProfitTargetTicks = 90;
                BigTradeRunnerTargetTicks = 200;
                BigTradeInitialStopTicks = 140;
                BigTradeUseAtrStopSizing = true;
                BigTradeAtrPeriod = 14;
                BigTradeAtrStopMultiplier = 1.2;
                BigTradeMinStopTicks = 90;
                BigTradeMaxStopTicks = 160;
                BigTradeMoveRunnerToBreakeven = true;
                BigTradeBreakevenPlusTicks = 2;
                BigTradeUseDailyLossLimit = true;
                BigTradeMaxDailyLosingTrades = 3;
                BigTradeDirectionMode = 1;
                BigTradeIgnoreInsideSpread = true;
                BigTradeUseEmaVwapFilter = true;
                BigTradeEmaFilterPeriod = 21;
                BigTradeUseEmaSlopeFilter = false;
                BigTradeEmaSlopeLookback = 3;
                BigTradeMinEmaSlopeTicks = 0;
                BigTradeUseAdxFilter = true;
                BigTradeAdxPeriod = 14;
                BigTradeMinAdx = 18;
                BigTradeOneSignalPerBar = true;

                ExportPerformanceCsv = true;
                PerformanceExportFolder = "strategy_exports";
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 60);
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                bollinger = Bollinger(BollingerStdDev, BollingerLength);
                macd = MACD(MacdFast, MacdSlow, MacdSmooth);
                bigTradeEmaFilter = EMA(BigTradeEmaFilterPeriod);
                bigTradeAtrFilter = ATR(BigTradeAtrPeriod);
                bigTradeAdxFilter = ADX(BigTradeAdxPeriod);
                InitializePerformanceExport();
            }
            else if (State == State.Terminated)
            {
                WritePerformanceSummary();
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (!EnableBigTradeFollower || e == null)
                return;

            if (e.MarketDataType == MarketDataType.Bid)
                bigTradeBestBid = e.Price;
            else if (e.MarketDataType == MarketDataType.Ask)
                bigTradeBestAsk = e.Price;
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                UpdateOneHourBias();
                return;
            }

            if (BarsInProgress == 2)
            {
                TryEnterTickBasedSignals();
                return;
            }

            if (BarsInProgress != 0 || BarsPeriod.BarsPeriodType != BarsPeriodType.Minute)
                return;

            UpdateBigTradeSessionReset();
            UpdateBigTradeSessionVwap();

            if (!HasRequiredBars())
                return;

            ManageMarketOpenProtection();
            ManageTradeTimeExit();
            ManageTimeExit();
            ManageBigTradeRunnerBreakeven();

            if (IsFirstTickOfBar && bbReversionStopReentryDirection != 0 && CurrentBars[0] > bbReversionStopReentryBar)
                ResetBbReversionStopReentry();
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null)
                return;

            Order order = execution.Order;
            if (order.OrderState != OrderState.Filled && order.OrderState != OrderState.PartFilled)
                return;

            string orderName = order.Name ?? string.Empty;
            if (IsEntrySignal(orderName))
            {
                ActiveTrade entryTrade = FindActiveTrade(orderName);
                if (entryTrade != null)
                    ApplyEntryFill(entryTrade, quantity, price, time);
                return;
            }

            string fromEntrySignal = order.FromEntrySignal ?? string.Empty;
            ActiveTrade trade = FindActiveTrade(fromEntrySignal);
            if (trade == null)
            {
                if (marketPosition == MarketPosition.Flat)
                    activeTrades.Clear();
                return;
            }

            bool stopExit = IsStopLossOrder(orderName);
            if (IsProfitTargetOrder(orderName))
            {
                trade.ProfitTargetFilled = true;
                RegisterBbPairProfitEvent(trade, time);
            }

            if (stopExit && !trade.ProfitTargetFilled)
                RegisterStopLoss(trade, time);

            ApplyExitFill(trade, orderName, quantity, price);
            ApplyExitQuantity(trade, fromEntrySignal, quantity);

            if (trade.IsClosed())
            {
                FinalizeBigTradeDailyLoss(trade);
                RegisterBbPairClosedTrade(trade, time);
                RecordClosedTrade(trade, time);
                activeTrades.Remove(trade);
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order == null)
                return;

            if ((orderState == OrderState.Cancelled || orderState == OrderState.Rejected) &&
                IsEntrySignal(order.Name))
            {
                ActiveTrade trade = FindActiveTrade(order.Name);
                if (trade != null)
                {
                    ApplyEntryCancellation(trade, order.Name, filled);
                    if (trade.IsClosed())
                        activeTrades.Remove(trade);
                }
            }

            if (orderState == OrderState.Rejected)
                HandleRejectedOrder(order, error, nativeError);
        }

        private void UpdateOneHourBias()
        {
            if (CurrentBars[1] < RequiredHourlyBars())
                return;

            double spanB;
            if (!TryGetSpanB(1, 1, out spanB))
            {
                oneHourBias = 0;
                return;
            }

            if (Opens[1][1] > spanB && Highs[1][1] > spanB)
                oneHourBias = 1;
            else if (Opens[1][1] < spanB && Highs[1][1] < spanB)
                oneHourBias = -1;
            else
                oneHourBias = 0;
        }

        private void TryEnterClosedBarSignals()
        {
            bool marketOpenBbReversionLongEntered = false;
            if (EnableBbReversion)
                marketOpenBbReversionLongEntered = TryEnterBbReversionMarketOpenLongOnly();

            if (EnableIchimokuBreakout)
                TryEnterIchimokuBreakout();

            if (EnableBbCross)
                TryEnterBbCross();

            if (EnableBbReversion && !marketOpenBbReversionLongEntered)
                TryEnterBbReversion(false);
        }

        private void TryProcessClosedBarSignalsFromTick()
        {
            DateTime tickMinute = TruncateToMinute(Times[2][0]);
            int barsAgo;

            if (TryFindPrimaryBarsAgoByTime(tickMinute, out barsAgo))
            {
                TryProcessClosedBarSignals(barsAgo);
                return;
            }

            if (TryFindPrimaryBarsAgoByTime(tickMinute.AddMinutes(-1), out barsAgo))
                TryProcessClosedBarSignals(barsAgo);
        }

        private void TryProcessClosedBarSignals(int barsAgo)
        {
            if (barsAgo < 0 || CurrentBars[0] < barsAgo + 1)
                return;

            DateTime closedBarTime = Times[0][barsAgo];
            if (closedBarTime == lastClosedSignalProcessedTime)
                return;

            lastClosedSignalProcessedTime = closedBarTime;

            int previousClosedSignalBarsAgo = closedSignalBarsAgo;
            closedSignalBarsAgo = barsAgo;

            if (bbReversionStopReentryDirection != 0 && CurrentBars[0] - barsAgo + 1 > bbReversionStopReentryBar)
                ResetBbReversionStopReentry();

            UpdateBbReversionMarketOpenLossNoEntryBlock();
            ManageBbReversionOppositeCloudExit();
            UpdateBbReversionCloudCooldown();

            if (CanSubmitAnyNewEntry())
                TryEnterClosedBarSignals();

            closedSignalBarsAgo = previousClosedSignalBarsAgo;
        }

        private bool TryFindPrimaryBarsAgoByTime(DateTime barTime, out int barsAgo)
        {
            int maxBarsAgo = Math.Min(CurrentBars[0], 3);
            for (int i = 0; i <= maxBarsAgo; i++)
            {
                if (Times[0][i] == barTime)
                {
                    barsAgo = i;
                    return true;
                }
            }

            barsAgo = -1;
            return false;
        }

        private void TryEnterTickBasedSignals()
        {
            if (!HasRequiredBars())
                return;

            TryProcessClosedBarSignalsFromTick();

            if (!CanSubmitAnyNewEntry())
                return;

            if (TryEnterBbReversionStopCandleReentry())
                return;

            if (EnableIchimokuTouch)
                TryEnterIchimokuTouch(Closes[2][0]);

            if (EnableBigTradeFollower)
                TryEnterBigTradeFollower(Times[2][0], Closes[2][0], Convert.ToInt64(Math.Round(Volumes[2][0])));
        }

        private bool TryEnterIchimokuTouch(double currentPrice)
        {
            if (HasActiveFamily(FamilyIchimokuTouch))
                return false;

            int primaryBar = CurrentBars[0];
            if (lastIchimokuTouchEntryBar == primaryBar)
                return false;

            double spanA;
            double spanB;
            if (!TryGetSpanA(0, 0, out spanA) || !TryGetSpanB(0, 0, out spanB))
                return false;

            bool longSetup = spanA > spanB &&
                Opens[0][0] > spanA &&
                RecentOpensAndClosesAboveBullishSpanA(TouchLookbackBars);

            if (longSetup && IsRealtimeSpanATouch(1, spanA, currentPrice) && (IsMacdFrontSide(0) || oneHourBias == 1))
            {
                double entryRef = currentPrice;
                double stop = entryRef - AverageRange(1, VolatilityLookbackBars) * IchimokuStopRangeMultiplier;
                if (SubmitBracketedEntry(1, IchimokuQuantity(), FamilyIchimokuTouch, entryRef, stop, false))
                {
                    lastIchimokuTouchEntryBar = primaryBar;
                    return true;
                }
            }

            return false;
        }

        private bool IsRealtimeSpanATouch(int direction, double spanA, double currentPrice)
        {
            double buffer = Math.Max(0, IchimokuTouchBufferTicks) * TickSize;

            if (direction == 1)
                return currentPrice <= spanA + buffer;

            if (direction == -1)
                return currentPrice >= spanA - buffer;

            return false;
        }

        private bool TryEnterIchimokuBreakout()
        {
            if (oneHourBias == 0 || HasActiveFamily(FamilyIchimokuBreakout))
                return false;

            int barsAgo = closedSignalBarsAgo;
            double entryRef = CurrentExecutionPrice();
            double averageRisk = AverageRange(barsAgo, VolatilityLookbackBars) * IchimokuStopRangeMultiplier;

            if ((!RequireBreakoutBiasDirection || oneHourBias == 1) &&
                MacdStayedBelowThenCrossedAbove(barsAgo, BreakoutMacdLookbackBars) &&
                IsIchimokuBreakoutLongLocation(barsAgo))
            {
                double rangeStop = entryRef - averageRisk;
                double threeBarStop = LowestLow(0, barsAgo, 3);
                double stop = Math.Min(rangeStop, threeBarStop);
                return SubmitBracketedEntry(1, IchimokuQuantity(), FamilyIchimokuBreakout, entryRef, stop, false);
            }

            if ((!RequireBreakoutBiasDirection || oneHourBias == -1) &&
                MacdStayedAboveThenCrossedBelow(barsAgo, BreakoutMacdLookbackBars) &&
                IsIchimokuBreakoutShortLocation(barsAgo))
            {
                double rangeStop = entryRef + averageRisk;
                double threeBarStop = HighestHigh(0, barsAgo, 3);
                double stop = Math.Max(rangeStop, threeBarStop);
                return SubmitBracketedEntry(-1, IchimokuQuantity(), FamilyIchimokuBreakout, entryRef, stop, false);
            }

            return false;
        }

        private bool TryEnterBbCross()
        {
            if (HasActiveFamily(FamilyBbCross))
                return false;

            if (TryEnterBbCrossDeferred())
                return true;

            int direction = 0;
            int barsAgo = closedSignalBarsAgo;
            if (IsBbCrossBaseCondition(1, barsAgo))
                direction = 1;
            else if (IsBbCrossBaseCondition(-1, barsAgo))
                direction = -1;

            if (direction == 0)
                return false;

            if (direction == 1 && CurrentSignalTime() < bbCrossDoubleLossBlockedUntil)
                return false;

            if (direction == 1 && IsBbReversionLongActiveDuringMarketOpenOverride())
                return false;

            if (IsBbPairEntryBlocked(FamilyBbCross, direction))
                return false;

            if (!CanSubmitNewEntry(direction))
                return false;

            int signalBar = CurrentBars[0] - barsAgo;
            double entryRef = CurrentExecutionPrice();
            double stop = direction == 1
                ? entryRef - AverageRange(barsAgo, VolatilityLookbackBars) * BbCrossStopRangeMultiplier
                : entryRef + AverageRange(barsAgo, VolatilityLookbackBars) * BbCrossStopRangeMultiplier;

            if (bbCrossLossReentryDirection == direction && signalBar <= bbCrossLossReentryUntilBar)
            {
                bbCrossLossReentryDirection = 0;
                bbCrossLossReentryUntilBar = -1;
                int reentryQuantity = direction == 1 ? BbBaseQuantity : BbBaseQuantity * DoubleReentryMultiplier;
                bool isDoubleReentry = direction != 1;
                return SubmitBracketedEntry(direction, reentryQuantity, FamilyBbCross, entryRef, stop, isDoubleReentry);
            }

            if (bbCrossLossReentryDirection != 0 && signalBar > bbCrossLossReentryUntilBar)
            {
                bbCrossLossReentryDirection = 0;
                bbCrossLossReentryUntilBar = -1;
            }

            if (!IsAgainstCloudForBbCross(direction, barsAgo))
                return SubmitBracketedEntry(direction, BbBaseQuantity, FamilyBbCross, entryRef, stop, false);

            if (HasPriorBbReversionCondition(direction, BbCrossPriorReversionBars, barsAgo + 1))
                return SubmitBracketedEntry(direction, BbBaseQuantity, FamilyBbCross, entryRef, stop, false);

            bbCrossDeferredDirection = direction;
            bbCrossDeferredStartBar = signalBar;
            return false;
        }

        private bool TryEnterBbCrossDeferred()
        {
            if (bbCrossDeferredDirection == 0 || bbCrossDeferredStartBar < 0 || HasActiveFamily(FamilyBbCross))
                return false;

            if (bbCrossDeferredDirection == 1 && CurrentSignalTime() < bbCrossDoubleLossBlockedUntil)
                return false;

            if (bbCrossDeferredDirection == 1 && IsBbReversionLongActiveDuringMarketOpenOverride())
                return false;

            if (IsBbPairEntryBlocked(FamilyBbCross, bbCrossDeferredDirection))
                return false;

            if (!CanSubmitNewEntry(bbCrossDeferredDirection))
                return false;

            int barsAgo = closedSignalBarsAgo;
            int signalBar = CurrentBars[0] - barsAgo;
            int elapsed = signalBar - bbCrossDeferredStartBar;

            if (HasMacdReverseZeroCross(bbCrossDeferredDirection, barsAgo) || elapsed > BbCrossDeferredMaxBars)
            {
                ResetBbCrossDeferred();
                return false;
            }

            if (elapsed >= BbCrossDeferredMinBars &&
                IsBbCrossMidlineCondition(bbCrossDeferredDirection, barsAgo))
            {
                int direction = bbCrossDeferredDirection;
                ResetBbCrossDeferred();

                double entryRef = CurrentExecutionPrice();
                double stop = direction == 1
                    ? entryRef - AverageRange(barsAgo, VolatilityLookbackBars) * BbCrossStopRangeMultiplier
                    : entryRef + AverageRange(barsAgo, VolatilityLookbackBars) * BbCrossStopRangeMultiplier;

                return SubmitBracketedEntry(direction, BbBaseQuantity, FamilyBbCross, entryRef, stop, false);
            }

            return false;
        }

        private bool TryEnterBbReversion(bool ignoreCooldowns)
        {
            if (HasActiveFamily(FamilyBbReversion))
                return false;

            int barsAgo = closedSignalBarsAgo;
            int signalBar = CurrentBars[0] - barsAgo;
            if (IsBbReversionMarketOpenLossNoEntryBlocked(signalBar))
                return false;

            bool rawLong = IsBbReversionCondition(1, barsAgo);
            bool rawShort = IsBbReversionCondition(-1, barsAgo);
            bool breakoutEntryCandle = IsCloudBreakoutClose(barsAgo) && (rawLong || rawShort);

            if (TryEnterBbReversionMarketOpenLongOnly())
                return true;

            if (!ignoreCooldowns &&
                CurrentSignalTime() < bbReversionCloudCooldownUntil &&
                CurrentBars[0] > bbReversionBypassCloudCooldownUntilBar &&
                !breakoutEntryCandle)
            {
                return false;
            }

            if (oneHourBias == 1 && rawLong && CanSubmitNewEntry(1))
                return TryEnterBbReversionDirection(1, barsAgo, false);

            if (oneHourBias == -1 && rawShort && CanSubmitNewEntry(-1))
                return TryEnterBbReversionDirection(-1, barsAgo, false);

            return false;
        }

        private bool TryEnterBbReversionMarketOpenLongOnly()
        {
            if (oneHourBias != 1 || HasActiveFamily(FamilyBbReversion) || !IsInMarketOpenLongOverrideWindow(CurrentSignalTime()))
                return false;

            int barsAgo = closedSignalBarsAgo;
            int signalBar = CurrentBars[0] - barsAgo;
            if (IsBbReversionMarketOpenLossNoEntryBlocked(signalBar))
                return false;

            if (!IsBbReversionCondition(1, barsAgo) || !CanSubmitNewEntry(1))
                return false;

            return TryEnterBbReversionMarketOpenLong(barsAgo);
        }

        private bool TryEnterBbReversionMarketOpenLong(int barsAgo)
        {
            double entryRef = CurrentExecutionPrice();
            double stop = entryRef - AverageRange(barsAgo, VolatilityLookbackBars) * BbStopRangeMultiplier;

            bool submitted = SubmitBracketedEntry(1, BbBaseQuantity, FamilyBbReversion, entryRef, stop, false, true);
            if (submitted)
            {
                ClearBbReversionFirstCondition(1);
                bbReversionBypassCloudCooldownUntilBar = -1;
            }

            return submitted;
        }

        private bool TryEnterBbReversionDirection(int direction, int barsAgo, bool forceDouble)
        {
            if (IsBbPairEntryBlocked(FamilyBbReversion, direction))
                return false;

            int signalBar = CurrentBars[0] - barsAgo;
            bool outsideCloud = direction == 1
                ? IsEntireBarBelowSpanB(barsAgo)
                : IsEntireBarAboveSpanB(barsAgo);

            if (!forceDouble && outsideCloud)
            {
                int firstBar = direction == 1 ? bbReversionLongFirstConditionBar : bbReversionShortFirstConditionBar;
                if (firstBar < 0 || signalBar - firstBar > BbReversionRetestMaxBars)
                {
                    SetBbReversionFirstCondition(direction, signalBar);
                    return false;
                }

                int elapsed = signalBar - firstBar;
                if (elapsed < BbReversionRetestMinBars || elapsed > BbReversionRetestMaxBars)
                    return false;
            }

            ClearBbReversionFirstCondition(direction);

            double entryRef = CurrentExecutionPrice();
            if (IsCurrentPriceInsideCloud(entryRef))
                return false;

            double stop = direction == 1
                ? entryRef - AverageRange(barsAgo, VolatilityLookbackBars) * BbStopRangeMultiplier
                : entryRef + AverageRange(barsAgo, VolatilityLookbackBars) * BbStopRangeMultiplier;

            int quantity = forceDouble ? BbBaseQuantity * DoubleReentryMultiplier : BbBaseQuantity;
            bool submitted = SubmitBracketedEntry(direction, quantity, FamilyBbReversion, entryRef, stop, forceDouble);
            if (submitted)
                bbReversionBypassCloudCooldownUntilBar = -1;

            return submitted;
        }

        private bool TryEnterBbReversionStopCandleReentry()
        {
            if (!EnableBbReversion || HasActiveFamily(FamilyBbReversion) || bbReversionStopReentryDirection == 0 || CurrentBars[0] != bbReversionStopReentryBar)
                return false;

            if (IsBbReversionMarketOpenLossNoEntryBlocked(CurrentBars[0]))
                return false;

            int direction = bbReversionStopReentryDirection;
            if (!IsBbReversionCondition(direction, 0))
                return false;

            double entryRef = CurrentExecutionPrice();
            double stop = direction == 1
                ? entryRef - AverageRange(0, VolatilityLookbackBars) * BbStopRangeMultiplier
                : entryRef + AverageRange(0, VolatilityLookbackBars) * BbStopRangeMultiplier;

            if (SubmitBracketedEntry(direction, BbBaseQuantity * DoubleReentryMultiplier, FamilyBbReversion, entryRef, stop, true, true))
            {
                ResetBbReversionStopReentry();
                return true;
            }

            return false;
        }

        private bool TryEnterBigTradeFollower(DateTime tickTime, double tradePrice, long tickVolume)
        {
            if (!EnableBigTradeFollower || HasActiveFamily(FamilyBigTradeFollower))
                return false;

            if (!CanSubmitAnyNewEntry())
                return false;

            if (BigTradeUseDailyLossLimit && bigTradeDailyLossLimitReached)
                return false;

            if (tickVolume < BigTradeVolume)
                return false;

            if (BigTradeOneSignalPerBar && bigTradeLastSignalPrimaryBar == CurrentBars[0])
                return false;

            double entryRef = RoundToTick(tradePrice);
            bool? isBuy = ClassifyBigTradeDirection(entryRef);
            if (isBuy == null)
                return false;

            int direction = isBuy.Value ? 1 : -1;
            if (!CanSubmitNewEntry(direction))
                return false;

            if (!PassesBigTradeEmaVwapFilter(direction))
                return false;

            if (!PassesBigTradeEmaSlopeFilter(direction))
                return false;

            if (!PassesBigTradeAdxFilter())
                return false;

            return SubmitBigTradeEntry(direction, entryRef, tickVolume);
        }

        private bool SubmitBigTradeEntry(int direction, double entryRef, long tickVolume)
        {
            if (direction == 0 || BigTradeQuantity <= 0 || HasActiveFamily(FamilyBigTradeFollower) || !CanSubmitNewEntry(direction))
                return false;

            int stopTicks = GetBigTradeInitialStopTicks();
            int leg1Quantity = GetBigTradePartialQuantity();
            int leg2Quantity = BigTradeQuantity - leg1Quantity;
            if (leg2Quantity <= 0)
                return false;

            string id = (++signalSequence).ToString();
            string leg1Signal = leg1Quantity > 0 ? FamilyBigTradeFollower + "_" + id + "_A" : string.Empty;
            string leg2Signal = FamilyBigTradeFollower + "_" + id + "_B";

            ActiveTrade trade = new ActiveTrade();
            trade.Family = FamilyBigTradeFollower;
            trade.SignalId = FamilyBigTradeFollower + "_" + id;
            trade.Leg1Signal = leg1Signal;
            trade.Leg2Signal = leg2Signal;
            trade.EntryTime = CurrentSignalTime();
            trade.Direction = direction;
            trade.IsDoubleReentry = false;
            trade.RequestedQuantity = BigTradeQuantity;
            trade.OpenLeg1Quantity = leg1Quantity;
            trade.OpenLeg2Quantity = leg2Quantity;
            trade.BigTradeInitialStopTicks = stopTicks;
            activeTrades.Add(trade);

            if (leg1Quantity > 0)
            {
                SetStopLoss(leg1Signal, CalculationMode.Ticks, stopTicks, false);
                SetProfitTarget(leg1Signal, CalculationMode.Ticks, BigTradeProfitTargetTicks);
                if (direction == 1)
                    EnterLong(2, leg1Quantity, leg1Signal);
                else
                    EnterShort(2, leg1Quantity, leg1Signal);
            }

            SetStopLoss(leg2Signal, CalculationMode.Ticks, stopTicks, false);
            SetProfitTarget(leg2Signal, CalculationMode.Ticks, BigTradeRunnerTargetTicks);
            if (direction == 1)
                EnterLong(2, leg2Quantity, leg2Signal);
            else
                EnterShort(2, leg2Quantity, leg2Signal);

            bigTradeLastSignalPrimaryBar = CurrentBars[0];
            return true;
        }

        private int GetBigTradePartialQuantity()
        {
            if (BigTradeQuantity <= 1 || BigTradeScaleOutQuantity <= 0)
                return 0;

            return Math.Min(BigTradeScaleOutQuantity, BigTradeQuantity - 1);
        }

        private int GetBigTradeInitialStopTicks()
        {
            int stopTicks = BigTradeInitialStopTicks;

            if (BigTradeUseAtrStopSizing && bigTradeAtrFilter != null && CurrentBars[0] >= BigTradeAtrPeriod && TickSize > 0)
            {
                int atrTicks = Convert.ToInt32(Math.Round((bigTradeAtrFilter[0] / TickSize) * BigTradeAtrStopMultiplier));
                stopTicks = Math.Max(BigTradeMinStopTicks, Math.Min(BigTradeMaxStopTicks, atrTicks));
            }

            return Math.Max(1, stopTicks);
        }

        private bool? ClassifyBigTradeDirection(double tradePrice)
        {
            if (BigTradeDirectionMode == 0 && State == State.Realtime && !double.IsNaN(bigTradeBestBid) && !double.IsNaN(bigTradeBestAsk))
            {
                if (tradePrice >= bigTradeBestAsk)
                    return true;

                if (tradePrice <= bigTradeBestBid)
                    return false;

                if (!BigTradeIgnoreInsideSpread)
                    return tradePrice >= (bigTradeBestBid + bigTradeBestAsk) * 0.5;
            }

            return ClassifyBigTradeByTickRule(tradePrice);
        }

        private bool? ClassifyBigTradeByTickRule(double tradePrice)
        {
            if (double.IsNaN(bigTradePreviousTradePrice))
            {
                bigTradePreviousTradePrice = tradePrice;
                return null;
            }

            bool? result;
            if (tradePrice > bigTradePreviousTradePrice)
            {
                result = true;
                bigTradePreviousTickRuleSide = true;
            }
            else if (tradePrice < bigTradePreviousTradePrice)
            {
                result = false;
                bigTradePreviousTickRuleSide = false;
            }
            else
            {
                result = bigTradePreviousTickRuleSide;
            }

            bigTradePreviousTradePrice = tradePrice;
            return result;
        }

        private bool PassesBigTradeEmaVwapFilter(int direction)
        {
            if (!BigTradeUseEmaVwapFilter)
                return true;

            if (bigTradeEmaFilter == null || CurrentBars[0] < BigTradeEmaFilterPeriod || bigTradeSessionVwap <= 0)
                return false;

            double price = Closes[0][0];
            if (direction == 1)
                return price > bigTradeEmaFilter[0] && price > bigTradeSessionVwap;

            if (direction == -1)
                return price < bigTradeEmaFilter[0] && price < bigTradeSessionVwap;

            return false;
        }

        private bool PassesBigTradeEmaSlopeFilter(int direction)
        {
            if (!BigTradeUseEmaSlopeFilter)
                return true;

            if (bigTradeEmaFilter == null || CurrentBars[0] < BigTradeEmaFilterPeriod + BigTradeEmaSlopeLookback)
                return false;

            double slopeTicks = (bigTradeEmaFilter[0] - bigTradeEmaFilter[BigTradeEmaSlopeLookback]) / TickSize;
            if (direction == 1)
                return slopeTicks >= BigTradeMinEmaSlopeTicks;

            if (direction == -1)
                return slopeTicks <= -BigTradeMinEmaSlopeTicks;

            return false;
        }

        private bool PassesBigTradeAdxFilter()
        {
            if (!BigTradeUseAdxFilter)
                return true;

            if (bigTradeAdxFilter == null || CurrentBars[0] < BigTradeAdxPeriod)
                return false;

            return bigTradeAdxFilter[0] >= BigTradeMinAdx;
        }

        private void UpdateBigTradeSessionReset()
        {
            if (!EnableBigTradeFollower || Bars == null || !Bars.IsFirstBarOfSession || bigTradeLastSessionResetBar == CurrentBars[0])
                return;

            bigTradeSessionCumPV = 0.0;
            bigTradeSessionCumVol = 0.0;
            bigTradeSessionVwap = Closes[0][0];
            bigTradeLastPrimaryBarVolume = 0.0;
            bigTradeLastVwapPrimaryBar = -1;
            bigTradePreviousTradePrice = double.NaN;
            bigTradePreviousTickRuleSide = null;
            bigTradeLastSignalPrimaryBar = -1;
            bigTradeLosingTradesThisSession = 0;
            bigTradeDailyLossLimitReached = false;
            bigTradeLastSessionResetBar = CurrentBars[0];
        }

        private void UpdateBigTradeSessionVwap()
        {
            if (!EnableBigTradeFollower || CurrentBars[0] < 0)
                return;

            if (CurrentBars[0] != bigTradeLastVwapPrimaryBar)
            {
                bigTradeLastVwapPrimaryBar = CurrentBars[0];
                bigTradeLastPrimaryBarVolume = 0.0;
            }

            double currentVolume = Volumes[0][0];
            double deltaVolume = currentVolume - bigTradeLastPrimaryBarVolume;
            if (deltaVolume <= 0)
                return;

            double typicalPrice = (Highs[0][0] + Lows[0][0] + Closes[0][0]) / 3.0;
            bigTradeSessionCumPV += typicalPrice * deltaVolume;
            bigTradeSessionCumVol += deltaVolume;

            if (bigTradeSessionCumVol > 0)
                bigTradeSessionVwap = bigTradeSessionCumPV / bigTradeSessionCumVol;

            bigTradeLastPrimaryBarVolume = currentVolume;
        }

        private void ManageBigTradeRunnerBreakeven()
        {
            if (!EnableBigTradeFollower || !BigTradeMoveRunnerToBreakeven || Position.MarketPosition == MarketPosition.Flat)
                return;

            List<ActiveTrade> trades = new List<ActiveTrade>(activeTrades);
            for (int i = 0; i < trades.Count; i++)
            {
                ActiveTrade trade = trades[i];
                if (trade.Family != FamilyBigTradeFollower || trade.BigTradeRunnerStopMoved || !trade.ProfitTargetFilled || trade.OpenLeg2Quantity <= 0 || trade.EntryQuantity <= 0)
                    continue;

                double breakevenStop = trade.Direction == 1
                    ? trade.AverageEntryPrice() + BigTradeBreakevenPlusTicks * TickSize
                    : trade.AverageEntryPrice() - BigTradeBreakevenPlusTicks * TickSize;

                breakevenStop = RoundToTick(breakevenStop);
                if (CanPlaceBigTradeRunnerStop(trade.Direction, breakevenStop))
                {
                    SetStopLoss(trade.Leg2Signal, CalculationMode.Price, breakevenStop, false);
                }
                else
                {
                    ExitBigTradeRunnerAtMarket(trade, "BigTradeRunnerBEExit");
                }

                trade.BigTradeRunnerStopMoved = true;
            }
        }

        private bool CanPlaceBigTradeRunnerStop(int direction, double stopPrice)
        {
            if (direction == 1)
                return stopPrice < CurrentBidOrClose() - TickSize;

            if (direction == -1)
                return stopPrice > CurrentAskOrClose() + TickSize;

            return false;
        }

        private void ExitBigTradeRunnerAtMarket(ActiveTrade trade, string reason)
        {
            if (trade == null || trade.OpenLeg2Quantity <= 0 || string.IsNullOrEmpty(trade.Leg2Signal))
                return;

            if (trade.Direction == 1)
                ExitLong(2, trade.OpenLeg2Quantity, reason, trade.Leg2Signal);
            else if (trade.Direction == -1)
                ExitShort(2, trade.OpenLeg2Quantity, reason, trade.Leg2Signal);
        }

        private void FinalizeBigTradeDailyLoss(ActiveTrade trade)
        {
            if (trade == null || trade.Family != FamilyBigTradeFollower || !BigTradeUseDailyLossLimit || BigTradeMaxDailyLosingTrades <= 0)
                return;

            if (trade.GrossPnL >= 0)
                return;

            bigTradeLosingTradesThisSession++;
            if (bigTradeLosingTradesThisSession >= BigTradeMaxDailyLosingTrades)
                bigTradeDailyLossLimitReached = true;
        }

        private bool SubmitBracketedEntry(int direction, int totalQuantity, string family, double entryRef, double stopPrice, bool doubleReentry, bool ignoreBbPairEntryBlock = false)
        {
            if (direction == 0 || totalQuantity <= 0 || HasActiveFamily(family) || !CanSubmitNewEntry(direction))
                return false;

            if (!ignoreBbPairEntryBlock && IsBbPairEntryBlocked(family, direction))
                return false;

            double risk = direction == 1 ? entryRef - stopPrice : stopPrice - entryRef;
            if (double.IsNaN(risk) || double.IsInfinity(risk) || risk < TickSize)
                return false;

            int riskTicks = PriceDistanceToTicks(risk);
            if (riskTicks < 1)
                return false;

            int target1Ticks = riskTicks;
            int target2Ticks = riskTicks * 2;

            int leg1Quantity = totalQuantity >= 2 ? totalQuantity / 2 : 0;
            int leg2Quantity = totalQuantity - leg1Quantity;
            string id = (++signalSequence).ToString();
            string leg1Signal = leg1Quantity > 0 ? family + "_" + id + "_A" : string.Empty;
            string leg2Signal = family + "_" + id + "_B";

            ActiveTrade trade = new ActiveTrade();
            trade.Family = family;
            trade.SignalId = family + "_" + id;
            trade.Leg1Signal = leg1Signal;
            trade.Leg2Signal = leg2Signal;
            trade.EntryTime = CurrentSignalTime();
            trade.Direction = direction;
            trade.IsDoubleReentry = doubleReentry;
            trade.BbReversionCloudExitEligible = IsBbReversionCloudExitEligible(family, direction, entryRef);
            trade.BbPairLongEntryBelowCloud = IsBbPairLongEntryBelowCloud(family, direction, entryRef);
            trade.RequestedQuantity = totalQuantity;
            trade.OpenLeg1Quantity = leg1Quantity;
            trade.OpenLeg2Quantity = leg2Quantity;
            activeTrades.Add(trade);

            if (leg1Quantity > 0)
            {
                SetStopLoss(leg1Signal, CalculationMode.Ticks, riskTicks, false);
                SetProfitTarget(leg1Signal, CalculationMode.Ticks, target1Ticks);
                if (direction == 1)
                    EnterLong(2, leg1Quantity, leg1Signal);
                else
                    EnterShort(2, leg1Quantity, leg1Signal);
            }

            SetStopLoss(leg2Signal, CalculationMode.Ticks, riskTicks, false);
            SetProfitTarget(leg2Signal, CalculationMode.Ticks, target2Ticks);
            if (direction == 1)
                EnterLong(2, leg2Quantity, leg2Signal);
            else
                EnterShort(2, leg2Quantity, leg2Signal);

            return true;
        }

        private void ManageMarketOpenProtection()
        {
            if (!UseMarketOpenProtection)
                return;

            DateTime openTime = DateWithTime(Time[0], MarketOpenTime);
            DateTime flattenTime = openTime.AddMinutes(-5);

            if (Time[0] >= flattenTime &&
                Time[0] < openTime &&
                Position.MarketPosition != MarketPosition.Flat &&
                lastOpenProtectionExitDate.Date != openTime.Date)
            {
                ExitActiveTrades("OpenProtectExit");
                lastOpenProtectionExitDate = openTime.Date;
            }
        }

        private void ManageTradeTimeExit()
        {
            if (!ExitAtTradeEnd || Position.MarketPosition == MarketPosition.Flat)
                return;

            DateTime endTime = DateWithTime(Time[0], TradeEndTime);
            if (Time[0] >= endTime && lastTradeEndExitDate.Date != endTime.Date)
            {
                ExitActiveTrades("TradeEndExit");
                lastTradeEndExitDate = endTime.Date;
            }
        }

        private void ManageTimeExit()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            List<ActiveTrade> trades = new List<ActiveTrade>(activeTrades);
            for (int i = 0; i < trades.Count; i++)
            {
                ActiveTrade trade = trades[i];
                if (trade.Family == FamilyBigTradeFollower)
                    continue;

                if (trade.EntryTime == DateTime.MinValue || trade.TimeExitSubmitted)
                    continue;

                if ((CurrentSignalTime() - trade.EntryTime).TotalMinutes >= TimeExitMinutes)
                    ExitTrade(trade, "TimeExit");
            }
        }

        private void ManageBbReversionOppositeCloudExit()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            double spanB;
            int barsAgo = closedSignalBarsAgo;
            if (!TryGetSpanB(0, barsAgo, out spanB))
                return;

            List<ActiveTrade> trades = new List<ActiveTrade>(activeTrades);
            for (int i = 0; i < trades.Count; i++)
            {
                ActiveTrade trade = trades[i];
                if (trade.Family != FamilyBbReversion)
                    continue;

                if (!trade.BbReversionCloudExitEligible)
                    continue;

                if (trade.Direction == 1 && Closes[0][barsAgo] < spanB)
                    ExitTrade(trade, "BBRevCloudExit");
                else if (trade.Direction == -1 && Closes[0][barsAgo] > spanB)
                    ExitTrade(trade, "BBRevCloudExit");
            }
        }

        private void UpdateBbReversionCloudCooldown()
        {
            if (!EnableBbReversion || CurrentBars[0] < 2)
                return;

            int barsAgo = closedSignalBarsAgo;
            bool entryCandle = IsBbReversionCondition(1, barsAgo) || IsBbReversionCondition(-1, barsAgo);
            if (IsCloudBreakoutClose(barsAgo) && !entryCandle)
                bbReversionCloudCooldownUntil = CurrentSignalTime().AddMinutes(BbReversionCooldownMinutes);
        }

        private void ExitActiveTrades(string reason)
        {
            if (activeTrades.Count == 0)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong(reason);
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort(reason);
                return;
            }

            List<ActiveTrade> trades = new List<ActiveTrade>(activeTrades);
            for (int i = 0; i < trades.Count; i++)
                ExitTrade(trades[i], reason);
        }

        private void ExitTrade(ActiveTrade trade, string reason)
        {
            if (trade == null || trade.TimeExitSubmitted)
                return;

            trade.TimeExitSubmitted = true;

            if (trade.Direction == 1)
            {
                if (!string.IsNullOrEmpty(trade.Leg1Signal) && trade.OpenLeg1Quantity > 0)
                    ExitLong(2, trade.OpenLeg1Quantity, reason + "A", trade.Leg1Signal);
                if (!string.IsNullOrEmpty(trade.Leg2Signal) && trade.OpenLeg2Quantity > 0)
                    ExitLong(2, trade.OpenLeg2Quantity, reason + "B", trade.Leg2Signal);
            }
            else if (trade.Direction == -1)
            {
                if (!string.IsNullOrEmpty(trade.Leg1Signal) && trade.OpenLeg1Quantity > 0)
                    ExitShort(2, trade.OpenLeg1Quantity, reason + "A", trade.Leg1Signal);
                if (!string.IsNullOrEmpty(trade.Leg2Signal) && trade.OpenLeg2Quantity > 0)
                    ExitShort(2, trade.OpenLeg2Quantity, reason + "B", trade.Leg2Signal);
            }
        }

        private void RegisterStopLoss(ActiveTrade trade, DateTime time)
        {
            if (trade == null || trade.StopRegistered)
                return;

            trade.StopRegistered = true;

            if (trade.Family == FamilyBbCross)
            {
                if (trade.IsDoubleReentry)
                {
                    if (trade.Direction == 1)
                        bbCrossDoubleLossBlockedUntil = time.AddMinutes(BbCrossDoubleLossBlockMinutes);

                    bbCrossLossReentryDirection = 0;
                    bbCrossLossReentryUntilBar = -1;
                }
                else
                {
                    bbCrossLossReentryDirection = trade.Direction;
                    bbCrossLossReentryUntilBar = CurrentBars[0] + BbCrossLossReentryBars;
                }
            }
            else if (trade.Family == FamilyBbReversion)
            {
                bbReversionBypassCloudCooldownUntilBar = CurrentBars[0] + BbReversionLossBypassBars;
                bbReversionLongFirstConditionBar = -1;
                bbReversionShortFirstConditionBar = -1;
                bbReversionStopReentryDirection = trade.Direction;
                bbReversionStopReentryBar = CurrentBars[0];

                if (IsInMarketOpenLongOverrideWindow(time))
                    bbReversionMarketOpenLossPendingBar = CurrentBars[0];
            }
        }

        private void RegisterBbPairProfitEvent(ActiveTrade trade, DateTime time)
        {
            if (trade == null || !IsBbPairFamily(trade.Family))
                return;

            ResetBbPairDailyLossStateIfNeeded(time);
            if (trade.Direction == 1)
            {
                bbPairLongBelowCloudLossPyramidBlocked = false;
                bbPairDailyConsecutiveLosses = 0;
            }
        }

        private void RegisterBbPairClosedTrade(ActiveTrade trade, DateTime exitTime)
        {
            if (trade == null || !IsBbPairFamily(trade.Family))
                return;

            ResetBbPairDailyLossStateIfNeeded(exitTime);

            if (trade.GrossPnL > 0)
            {
                if (trade.Direction == 1)
                {
                    bbPairLongBelowCloudLossPyramidBlocked = false;
                    bbPairDailyConsecutiveLosses = 0;
                }

                return;
            }

            if (trade.GrossPnL >= 0 || trade.ProfitTargetFilled)
                return;

            if (trade.Direction != 1)
                return;

            if (trade.BbPairLongEntryBelowCloud)
                bbPairLongBelowCloudLossPyramidBlocked = true;

            bbPairDailyConsecutiveLosses++;
            if (BbPairDailyLossLimit > 0 && bbPairDailyConsecutiveLosses >= BbPairDailyLossLimit)
            {
                bbPairEntryBlockedUntil = exitTime.AddMinutes(BbPairDailyLossBlockMinutes);
                bbPairDailyConsecutiveLosses = 0;
            }
        }

        private bool IsBbPairEntryBlocked(string family, int direction)
        {
            if (!IsBbPairFamily(family))
                return false;

            DateTime signalTime = CurrentSignalTime();
            ResetBbPairDailyLossStateIfNeeded(signalTime);

            if (direction == 1 && signalTime < bbPairEntryBlockedUntil)
                return true;

            if (direction == 1 && bbPairLongBelowCloudLossPyramidBlocked && HasActiveBbPairTrade())
                return true;

            return false;
        }

        private bool IsBbPairFamily(string family)
        {
            return family == FamilyBbCross || family == FamilyBbReversion;
        }

        private bool HasActiveBbPairTrade()
        {
            for (int i = 0; i < activeTrades.Count; i++)
            {
                ActiveTrade trade = activeTrades[i];
                if (!trade.IsClosed() && IsBbPairFamily(trade.Family))
                    return true;
            }

            return false;
        }

        private bool IsBbPairLongEntryBelowCloud(string family, int direction, double entryPrice)
        {
            if (!IsBbPairFamily(family) || direction != 1)
                return false;

            double spanB;
            if (!TryGetSpanB(0, 0, out spanB))
                return false;

            return entryPrice < spanB;
        }

        private void ResetBbPairDailyLossStateIfNeeded(DateTime time)
        {
            if (bbPairDailyLossDate.Date == time.Date)
                return;

            bbPairDailyLossDate = time.Date;
            bbPairDailyConsecutiveLosses = 0;
            bbPairEntryBlockedUntil = DateTime.MinValue;
        }

        private bool CanSubmitAnyNewEntry()
        {
            DateTime signalTime = CurrentSignalTime();
            return IsInTradeTimeWindow(signalTime) && !IsInMarketOpenNoEntryWindow(signalTime);
        }

        private bool CanSubmitNewEntry(int direction)
        {
            DateTime signalTime = CurrentSignalTime();
            if (direction == 0 || !IsInTradeTimeWindow(signalTime) || IsInMarketOpenNoEntryWindow(signalTime))
                return false;

            int currentDirection = CurrentActiveDirection();
            return currentDirection == 0 || currentDirection == direction;
        }

        private int CurrentActiveDirection()
        {
            if (Position.MarketPosition == MarketPosition.Long)
                return 1;

            if (Position.MarketPosition == MarketPosition.Short)
                return -1;

            for (int i = 0; i < activeTrades.Count; i++)
            {
                if (!activeTrades[i].IsClosed())
                    return activeTrades[i].Direction;
            }

            return 0;
        }

        private bool HasActiveFamily(string family)
        {
            if (string.IsNullOrEmpty(family))
                return false;

            for (int i = 0; i < activeTrades.Count; i++)
            {
                ActiveTrade trade = activeTrades[i];
                if (!trade.IsClosed() && trade.Family == family)
                    return true;
            }

            return false;
        }

        private void HandleRejectedOrder(Order order, ErrorCode error, string nativeError)
        {
            string orderName = order.Name ?? string.Empty;
            PrintRejectedOrder(orderName, order.FromEntrySignal ?? string.Empty, error, nativeError);

            if (IsEntrySignal(orderName))
                return;

            if (!IsStopLossOrder(orderName) && !IsProfitTargetOrder(orderName))
                return;

            string fromEntrySignal = order.FromEntrySignal ?? string.Empty;
            ActiveTrade trade = FindActiveTrade(fromEntrySignal);
            if (trade == null)
                return;

            ExitTradeLegAtMarket(trade, fromEntrySignal, IsStopLossOrder(orderName) ? "RejectedStopExit" : "RejectedTargetExit");
        }

        private void PrintRejectedOrder(string orderName, string fromEntrySignal, ErrorCode error, string nativeError)
        {
            string message = "Order rejected: " + orderName + ", fromEntry=" + fromEntrySignal + ", error=" + error;
            if (!string.IsNullOrWhiteSpace(nativeError))
                message += ", nativeError=" + nativeError;

            Print(message);
        }

        private void ExitTradeLegAtMarket(ActiveTrade trade, string fromEntrySignal, string reason)
        {
            if (trade == null || string.IsNullOrEmpty(fromEntrySignal) || Position.MarketPosition == MarketPosition.Flat)
                return;

            int quantity = 0;
            if (fromEntrySignal == trade.Leg1Signal)
                quantity = trade.OpenLeg1Quantity;
            else if (fromEntrySignal == trade.Leg2Signal)
                quantity = trade.OpenLeg2Quantity;

            if (quantity <= 0)
                return;

            if (trade.Direction == 1)
                ExitLong(2, quantity, reason, fromEntrySignal);
            else if (trade.Direction == -1)
                ExitShort(2, quantity, reason, fromEntrySignal);
        }

        private bool HasActiveFamilyDirection(string family, int direction)
        {
            if (string.IsNullOrEmpty(family) || direction == 0)
                return false;

            for (int i = 0; i < activeTrades.Count; i++)
            {
                ActiveTrade trade = activeTrades[i];
                if (!trade.IsClosed() && trade.Family == family && trade.Direction == direction)
                    return true;
            }

            return false;
        }

        private bool IsBbReversionLongActiveDuringMarketOpenOverride()
        {
            return IsInMarketOpenLongOverrideWindow(CurrentSignalTime()) && HasActiveFamilyDirection(FamilyBbReversion, 1);
        }

        private void UpdateBbReversionMarketOpenLossNoEntryBlock()
        {
            if (bbReversionMarketOpenLossPendingBar < 0 || CurrentBars[0] <= bbReversionMarketOpenLossPendingBar)
                return;

            int pendingBarsAgo = CurrentBars[0] - bbReversionMarketOpenLossPendingBar;
            bool entryCandle = IsBbReversionCondition(1, pendingBarsAgo) || IsBbReversionCondition(-1, pendingBarsAgo);
            if (!entryCandle)
                bbReversionMarketOpenLossNoEntryUntilBar = bbReversionMarketOpenLossPendingBar + 2;

            bbReversionMarketOpenLossPendingBar = -1;
        }

        private bool IsBbReversionMarketOpenLossNoEntryBlocked(int signalBar)
        {
            if (bbReversionMarketOpenLossNoEntryUntilBar < 0)
                return false;

            if (signalBar <= bbReversionMarketOpenLossNoEntryUntilBar)
                return true;

            bbReversionMarketOpenLossNoEntryUntilBar = -1;
            return false;
        }

        private bool IsBbReversionCloudExitEligible(string family, int direction, double entryPrice)
        {
            if (family != FamilyBbReversion)
                return false;

            double spanB;
            if (!TryGetSpanB(0, 0, out spanB))
                return false;

            if (direction == 1)
                return entryPrice > spanB;

            if (direction == -1)
                return entryPrice < spanB;

            return false;
        }

        private bool IsCurrentPriceInsideCloud(double price)
        {
            double spanA;
            double spanB;
            if (!TryGetSpanA(0, 0, out spanA) || !TryGetSpanB(0, 0, out spanB))
                return false;

            double upper = Math.Max(spanA, spanB);
            double lower = Math.Min(spanA, spanB);
            return price >= lower && price <= upper;
        }

        private bool IsInTradeTimeWindow(DateTime time)
        {
            DateTime startTime = DateWithTime(time, TradeStartTime);
            DateTime endTime = DateWithTime(time, TradeEndTime);

            if (startTime <= endTime)
                return time >= startTime && time < endTime;

            return time >= startTime || time < endTime;
        }

        private ActiveTrade FindActiveTrade(string entrySignal)
        {
            if (string.IsNullOrEmpty(entrySignal))
                return null;

            for (int i = 0; i < activeTrades.Count; i++)
            {
                ActiveTrade trade = activeTrades[i];
                if (entrySignal == trade.Leg1Signal || entrySignal == trade.Leg2Signal)
                    return trade;
            }

            return null;
        }

        private void ApplyExitQuantity(ActiveTrade trade, string fromEntrySignal, int quantity)
        {
            if (trade == null || quantity <= 0)
                return;

            if (fromEntrySignal == trade.Leg1Signal)
                trade.OpenLeg1Quantity = Math.Max(0, trade.OpenLeg1Quantity - quantity);
            else if (fromEntrySignal == trade.Leg2Signal)
                trade.OpenLeg2Quantity = Math.Max(0, trade.OpenLeg2Quantity - quantity);
        }

        private void ApplyEntryCancellation(ActiveTrade trade, string entrySignal, int filled)
        {
            if (trade == null)
                return;

            int openQuantity = Math.Max(0, filled);
            if (entrySignal == trade.Leg1Signal)
                trade.OpenLeg1Quantity = openQuantity;
            else if (entrySignal == trade.Leg2Signal)
                trade.OpenLeg2Quantity = openQuantity;
        }

        private void ApplyEntryFill(ActiveTrade trade, int quantity, double price, DateTime time)
        {
            if (trade == null || quantity <= 0)
                return;

            if (trade.EntryQuantity == 0)
                trade.EntryTime = time;

            trade.EntryQuantity += quantity;
            trade.EntryValue += price * quantity;
        }

        private void ApplyExitFill(ActiveTrade trade, string exitName, int quantity, double price)
        {
            if (trade == null || quantity <= 0 || trade.EntryQuantity <= 0)
                return;

            double averageEntry = trade.AverageEntryPrice();
            double pointMove = trade.Direction * (price - averageEntry);
            double totalPoints = pointMove * quantity;
            double pnl = totalPoints * Instrument.MasterInstrument.PointValue;

            trade.ExitQuantity += quantity;
            trade.ExitValue += price * quantity;
            trade.GrossPoints += totalPoints;
            trade.GrossPnL += pnl;
            trade.LastExitReason = NormalizeExitReason(exitName);
        }

        private void RecordClosedTrade(ActiveTrade trade, DateTime exitTime)
        {
            if (!ExportPerformanceCsv || trade == null || trade.EntryQuantity <= 0 || trade.ExitQuantity <= 0)
                return;

            StrategySummary summary = GetPerformanceSummary(trade.Family);
            summary.Trades++;
            summary.Quantity += trade.EntryQuantity;
            double notional = trade.AverageEntryPrice() * Instrument.MasterInstrument.PointValue * trade.EntryQuantity;
            summary.Notional += notional;
            summary.NetProfit += trade.GrossPnL;
            summary.NetPoints += trade.GrossPoints;

            if (trade.GrossPnL > 0)
            {
                summary.Winners++;
                summary.GrossProfit += trade.GrossPnL;
            }
            else if (trade.GrossPnL < 0)
            {
                summary.Losers++;
                summary.GrossLoss += trade.GrossPnL;
            }
            else
            {
                summary.Breakeven++;
            }

            if (!performanceExportReady)
                return;

            AppendCsvLine(tradeExportPath, string.Join(",",
                Csv(TimeToText(trade.EntryTime)),
                Csv(TimeToText(exitTime)),
                Csv(trade.Family),
                Csv(trade.SignalId),
                Csv(trade.Direction == 1 ? "Long" : "Short"),
                trade.EntryQuantity.ToString(CultureInfo.InvariantCulture),
                FormatNumber(trade.AverageEntryPrice()),
                FormatNumber(trade.AverageExitPrice()),
                FormatNumber(trade.GrossPoints),
                FormatNumber(trade.GrossPnL),
                FormatNumber(trade.GrossPnL / Math.Max(1, trade.EntryQuantity)),
                FormatNumber(notional <= 0 ? 0 : trade.GrossPnL / notional),
                Csv(trade.LastExitReason),
                Csv(trade.IsDoubleReentry ? "Y" : "N")));
        }

        private StrategySummary GetPerformanceSummary(string family)
        {
            StrategySummary summary;
            if (!performanceSummaries.TryGetValue(family, out summary))
            {
                summary = new StrategySummary();
                performanceSummaries[family] = summary;
            }

            return summary;
        }

        private bool IsInMarketOpenNoEntryWindow(DateTime time)
        {
            if (!UseMarketOpenProtection)
                return false;

            DateTime openTime = DateWithTime(time, MarketOpenTime);
            return time >= openTime.AddMinutes(-15) && time < openTime.AddMinutes(5);
        }

        private bool IsInMarketOpenLongOverrideWindow(DateTime time)
        {
            DateTime openTime = DateWithTime(time, MarketOpenTime);
            return time >= openTime.AddMinutes(MarketOpenLongOverrideStartMinutes) &&
                time < openTime.AddMinutes(MarketOpenLongOverrideEndMinutes);
        }

        private bool RecentOpensAndClosesAboveBullishSpanA(int lookback)
        {
            for (int i = 1; i <= lookback; i++)
            {
                double spanA;
                double spanB;
                if (!TryGetSpanA(0, i, out spanA) || !TryGetSpanB(0, i, out spanB))
                    return false;

                if (spanA <= spanB || Opens[0][i] <= spanA || Closes[0][i] <= spanA)
                    return false;
            }

            return true;
        }

        private bool RecentOpensBelowBearishSpanA(int lookback)
        {
            for (int i = 1; i <= lookback; i++)
            {
                double spanA;
                double spanB;
                if (!TryGetSpanA(0, i, out spanA) || !TryGetSpanB(0, i, out spanB))
                    return false;

                if (spanA >= spanB || Opens[0][i] >= spanA)
                    return false;
            }

            return true;
        }

        private bool IsIchimokuBreakoutLongLocation(int barsAgo)
        {
            double spanA;
            double spanB;
            if (!TryGetSpanA(0, barsAgo, out spanA) || !TryGetSpanB(0, barsAgo, out spanB))
                return false;

            double close = Closes[0][barsAgo];
            if (spanA > spanB)
                return close >= spanB && close <= spanA;

            return close > spanA;
        }

        private bool IsIchimokuBreakoutShortLocation(int barsAgo)
        {
            double spanA;
            double spanB;
            if (!TryGetSpanA(0, barsAgo, out spanA) || !TryGetSpanB(0, barsAgo, out spanB))
                return false;

            double close = Closes[0][barsAgo];
            if (spanA < spanB)
                return close >= spanA && close <= spanB;

            return close < spanA;
        }

        private bool IsBbCrossBaseCondition(int direction, int barsAgo)
        {
            if (direction == 1)
                return MacdCrossedAboveZero(barsAgo) && IsBbCrossMidlineCondition(1, barsAgo);

            return MacdCrossedBelowZero(barsAgo) && IsBbCrossMidlineCondition(-1, barsAgo);
        }

        private bool IsBbCrossMidlineCondition(int direction, int barsAgo)
        {
            double middle = bollinger.Middle[barsAgo];
            double closeTolerance = Math.Max(0, BbCrossMiddleCloseTolerancePoints);

            if (direction == 1)
                return Opens[0][barsAgo] < middle && Closes[0][barsAgo] <= middle + closeTolerance;

            return Opens[0][barsAgo] > middle && Closes[0][barsAgo] >= middle - closeTolerance;
        }

        private bool HasPriorBbReversionCondition(int direction, int bars, int startBarsAgo)
        {
            for (int i = startBarsAgo; i < startBarsAgo + bars; i++)
            {
                if (IsBbReversionCondition(direction, i))
                    return true;
            }

            return false;
        }

        private bool IsAgainstCloudForBbCross(int direction, int startBarsAgo)
        {
            for (int i = startBarsAgo; i < startBarsAgo + BbCrossCloudLookbackBars; i++)
            {
                double spanB;
                if (!TryGetSpanB(0, i, out spanB))
                    return false;

                if (direction == 1)
                {
                    if (!(Highs[0][i] < spanB && Lows[0][i] < spanB))
                        return false;
                }
                else
                {
                    if (!(Highs[0][i] > spanB && Lows[0][i] > spanB))
                        return false;
                }
            }

            return true;
        }

        private bool IsBbReversionCondition(int direction, int barsAgo)
        {
            if (direction == 1)
                return Lows[0][barsAgo] < bollinger.Lower[barsAgo] &&
                    Closes[0][barsAgo] > bollinger.Lower[barsAgo];

            return Highs[0][barsAgo] > bollinger.Upper[barsAgo] &&
                Closes[0][barsAgo] < bollinger.Upper[barsAgo];
        }

        private bool IsCloudBreakoutClose(int barsAgo)
        {
            if (CurrentBars[0] < barsAgo + 1)
                return false;

            double previousSpanB;
            double currentSpanB;
            if (!TryGetSpanB(0, barsAgo + 1, out previousSpanB) ||
                !TryGetSpanB(0, barsAgo, out currentSpanB))
            {
                return false;
            }

            double previousOpen = Opens[0][barsAgo + 1];
            double currentClose = Closes[0][barsAgo];

            return (previousOpen > previousSpanB && currentClose < currentSpanB) ||
                (previousOpen < previousSpanB && currentClose > currentSpanB);
        }

        private bool IsEntireBarBelowSpanB(int barsAgo)
        {
            double spanB;
            return TryGetSpanB(0, barsAgo, out spanB) &&
                Highs[0][barsAgo] < spanB &&
                Lows[0][barsAgo] < spanB;
        }

        private bool IsEntireBarAboveSpanB(int barsAgo)
        {
            double spanB;
            return TryGetSpanB(0, barsAgo, out spanB) &&
                Highs[0][barsAgo] > spanB &&
                Lows[0][barsAgo] > spanB;
        }

        private bool IsMacdFrontSide(int barsAgo)
        {
            return macd.Default[barsAgo] > 0 && macd.Avg[barsAgo] > 0;
        }

        private bool IsMacdBackSide(int barsAgo)
        {
            return macd.Default[barsAgo] < 0 && macd.Avg[barsAgo] < 0;
        }

        private bool MacdCrossedAboveZero(int barsAgo)
        {
            return MacdHistogram(barsAgo) > 0 && MacdHistogram(barsAgo + 1) <= 0;
        }

        private bool MacdCrossedBelowZero(int barsAgo)
        {
            return MacdHistogram(barsAgo) < 0 && MacdHistogram(barsAgo + 1) >= 0;
        }

        private bool MacdStayedBelowThenCrossedAbove(int barsAgo, int lookback)
        {
            if (MacdHistogram(barsAgo) <= 0)
                return false;

            for (int i = barsAgo + 1; i <= barsAgo + lookback; i++)
            {
                if (MacdHistogram(i) >= 0)
                    return false;
            }

            return true;
        }

        private bool MacdStayedAboveThenCrossedBelow(int barsAgo, int lookback)
        {
            if (MacdHistogram(barsAgo) >= 0)
                return false;

            for (int i = barsAgo + 1; i <= barsAgo + lookback; i++)
            {
                if (MacdHistogram(i) <= 0)
                    return false;
            }

            return true;
        }

        private double MacdHistogram(int barsAgo)
        {
            return macd.Diff[barsAgo];
        }

        private bool HasMacdReverseZeroCross(int originalDirection, int barsAgo)
        {
            if (originalDirection == 1)
                return MacdCrossedBelowZero(barsAgo);

            return MacdCrossedAboveZero(barsAgo);
        }

        private bool TryGetSpanA(int barsInProgressIndex, int barsAgo, out double spanA)
        {
            spanA = 0;
            int calcBarsAgo = barsAgo + (UseDisplacedIchimokuCloud ? IchimokuDisplacement : 0);

            double tenkan;
            double kijun;
            if (!TryGetMidpoint(barsInProgressIndex, calcBarsAgo, IchimokuTenkanPeriod, out tenkan) ||
                !TryGetMidpoint(barsInProgressIndex, calcBarsAgo, IchimokuKijunPeriod, out kijun))
            {
                return false;
            }

            spanA = (tenkan + kijun) / 2.0;
            return true;
        }

        private bool TryGetSpanB(int barsInProgressIndex, int barsAgo, out double spanB)
        {
            spanB = 0;
            int calcBarsAgo = barsAgo + (UseDisplacedIchimokuCloud ? IchimokuDisplacement : 0);
            return TryGetMidpoint(barsInProgressIndex, calcBarsAgo, IchimokuSenkouBPeriod, out spanB);
        }

        private bool TryGetMidpoint(int barsInProgressIndex, int barsAgo, int period, out double midpoint)
        {
            midpoint = 0;
            if (CurrentBars[barsInProgressIndex] < barsAgo + period - 1)
                return false;

            double high = HighestHigh(barsInProgressIndex, barsAgo, period);
            double low = LowestLow(barsInProgressIndex, barsAgo, period);
            midpoint = (high + low) / 2.0;
            return true;
        }

        private double HighestHigh(int barsInProgressIndex, int barsAgo, int period)
        {
            double value = double.MinValue;
            for (int i = barsAgo; i < barsAgo + period; i++)
                value = Math.Max(value, Highs[barsInProgressIndex][i]);

            return value;
        }

        private double LowestLow(int barsInProgressIndex, int barsAgo, int period)
        {
            double value = double.MaxValue;
            for (int i = barsAgo; i < barsAgo + period; i++)
                value = Math.Min(value, Lows[barsInProgressIndex][i]);

            return value;
        }

        private double AverageRange(int barsAgo, int period)
        {
            double sum = 0;
            for (int i = barsAgo; i < barsAgo + period; i++)
                sum += Highs[0][i] - Lows[0][i];

            return sum / period;
        }

        private bool HasRequiredBars()
        {
            return CurrentBars[0] >= RequiredPrimaryBars() &&
                CurrentBars[1] >= RequiredHourlyBars() &&
                CurrentBars[2] >= 0;
        }

        private int RequiredPrimaryBars()
        {
            int ichimoku = Math.Max(Math.Max(IchimokuTenkanPeriod, IchimokuKijunPeriod), IchimokuSenkouBPeriod);
            if (UseDisplacedIchimokuCloud)
                ichimoku += IchimokuDisplacement;

            int macdRequired = MacdSlow + MacdSmooth + BreakoutMacdLookbackBars + 5;
            int bbRequired = BollingerLength + Math.Max(BbCrossCloudLookbackBars, BbReversionRetestMaxBars) + 5;
            int bigTradeRequired = Math.Max(BigTradeEmaFilterPeriod + BigTradeEmaSlopeLookback + 5, Math.Max(BigTradeAtrPeriod, BigTradeAdxPeriod) + 5);
            int required = Math.Max(
                ichimoku + Math.Max(TouchLookbackBars, BbCrossCloudLookbackBars) + 5,
                Math.Max(Math.Max(macdRequired, bbRequired), bigTradeRequired));
            return Math.Max(BarsRequiredToTrade, required);
        }

        private int RequiredHourlyBars()
        {
            int ichimoku = Math.Max(Math.Max(IchimokuTenkanPeriod, IchimokuKijunPeriod), IchimokuSenkouBPeriod);
            if (UseDisplacedIchimokuCloud)
                ichimoku += IchimokuDisplacement;

            return ichimoku + 2;
        }

        private int IchimokuQuantity()
        {
            return Math.Max(1, BbBaseQuantity * IchimokuQuantityMultiplier);
        }

        private double RoundToTick(double price)
        {
            return Instrument.MasterInstrument.RoundToTickSize(price);
        }

        private int PriceDistanceToTicks(double distance)
        {
            if (TickSize <= 0 || double.IsNaN(distance) || double.IsInfinity(distance) || distance <= 0)
                return 0;

            return Math.Max(1, (int)Math.Ceiling(distance / TickSize));
        }

        private double CurrentExecutionPrice()
        {
            if (CurrentBars.Length > 2 && CurrentBars[2] >= 0 && BarsInProgress == 2)
                return Closes[2][0];

            return Opens[0][0];
        }

        private double CurrentBidOrClose()
        {
            double bid = GetCurrentBid();
            if (IsValidMarketPrice(bid))
                return bid;

            if (IsValidMarketPrice(bigTradeBestBid))
                return bigTradeBestBid;

            return Closes[0][0];
        }

        private double CurrentAskOrClose()
        {
            double ask = GetCurrentAsk();
            if (IsValidMarketPrice(ask))
                return ask;

            if (IsValidMarketPrice(bigTradeBestAsk))
                return bigTradeBestAsk;

            return Closes[0][0];
        }

        private bool IsValidMarketPrice(double price)
        {
            return price > 0 && !double.IsNaN(price) && !double.IsInfinity(price);
        }

        private void InitializePerformanceExport()
        {
            if (!ExportPerformanceCsv)
                return;

            try
            {
                string exportFolder = PerformanceExportFolder;
                if (string.IsNullOrWhiteSpace(exportFolder))
                    exportFolder = "strategy_exports";

                if (!Path.IsPathRooted(exportFolder))
                    exportFolder = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, exportFolder);

                Directory.CreateDirectory(exportFolder);

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string prefix = SafeFileName(Name + "_" + Instrument.FullName.Replace(" ", "_") + "_" + stamp);
                tradeExportPath = Path.Combine(exportFolder, prefix + "_trades.csv");
                summaryExportPath = Path.Combine(exportFolder, prefix + "_summary.csv");

                File.WriteAllText(tradeExportPath,
                    "EntryTime,ExitTime,StrategyFamily,SignalId,Direction,Quantity,AvgEntry,AvgExit,GrossPoints,GrossPnL,GrossPnLPerContract,ReturnPctOnNotional,ExitReason,DoubleReentry" + Environment.NewLine);

                performanceExportReady = true;
            }
            catch (Exception exception)
            {
                performanceExportReady = false;
                Print("Performance CSV export disabled: " + exception.Message);
            }
        }

        private void WritePerformanceSummary()
        {
            if (!ExportPerformanceCsv || !performanceExportReady)
                return;

            try
            {
                AppendCsvLine(summaryExportPath,
                    "StrategyFamily,Trades,Winners,Losers,Breakeven,WinRate,Quantity,Notional,GrossProfit,GrossLoss,NetProfit,NetPoints,NetReturnPctOnNotional,AvgPnL,ProfitFactor");

                foreach (KeyValuePair<string, StrategySummary> item in performanceSummaries)
                {
                    StrategySummary summary = item.Value;
                    double winRate = summary.Trades <= 0 ? 0 : (double)summary.Winners / summary.Trades;
                    double averagePnl = summary.Trades <= 0 ? 0 : summary.NetProfit / summary.Trades;
                    double returnPct = summary.Notional <= 0 ? 0 : summary.NetProfit / summary.Notional;
                    double profitFactor = summary.GrossLoss == 0
                        ? (summary.GrossProfit > 0 ? 999.0 : 0.0)
                        : summary.GrossProfit / Math.Abs(summary.GrossLoss);

                    AppendCsvLine(summaryExportPath, string.Join(",",
                        Csv(item.Key),
                        summary.Trades.ToString(CultureInfo.InvariantCulture),
                        summary.Winners.ToString(CultureInfo.InvariantCulture),
                        summary.Losers.ToString(CultureInfo.InvariantCulture),
                        summary.Breakeven.ToString(CultureInfo.InvariantCulture),
                        FormatNumber(winRate),
                        summary.Quantity.ToString(CultureInfo.InvariantCulture),
                        FormatNumber(summary.Notional),
                        FormatNumber(summary.GrossProfit),
                        FormatNumber(summary.GrossLoss),
                        FormatNumber(summary.NetProfit),
                        FormatNumber(summary.NetPoints),
                        FormatNumber(returnPct),
                        FormatNumber(averagePnl),
                        FormatNumber(profitFactor)));
                }

                Print("Performance CSV exported: " + tradeExportPath);
                Print("Performance summary exported: " + summaryExportPath);
            }
            catch (Exception exception)
            {
                Print("Performance summary export failed: " + exception.Message);
            }
        }

        private string NormalizeExitReason(string orderName)
        {
            if (string.IsNullOrEmpty(orderName))
                return string.Empty;

            if (IsStopLossOrder(orderName))
                return "StopLoss";

            if (IsProfitTargetOrder(orderName))
                return "ProfitTarget";

            return orderName;
        }

        private string TimeToText(DateTime time)
        {
            return time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private string FormatNumber(double value)
        {
            return value.ToString("0.########", CultureInfo.InvariantCulture);
        }

        private string Csv(string value)
        {
            if (value == null)
                value = string.Empty;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private string SafeFileName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "strategy_export";

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                value = value.Replace(invalidChar, '_');

            return value;
        }

        private void AppendCsvLine(string path, string line)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception exception)
            {
                performanceExportReady = false;
                Print("Performance CSV append failed: " + exception.Message);
            }
        }

        private DateTime CurrentSignalTime()
        {
            if (CurrentBars.Length > 2 && CurrentBars[2] >= 0 && BarsInProgress == 2)
                return Times[2][0];

            return Times[0][0];
        }

        private DateTime TruncateToMinute(DateTime time)
        {
            return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
        }

        private DateTime DateWithTime(DateTime date, int hhmmss)
        {
            int hour = hhmmss / 10000;
            int minute = (hhmmss / 100) % 100;
            int second = hhmmss % 100;
            return new DateTime(date.Year, date.Month, date.Day, hour, minute, second);
        }

        private bool IsEntrySignal(string orderName)
        {
            return StartsWith(orderName, FamilyIchimokuTouch) ||
                StartsWith(orderName, FamilyIchimokuBreakout) ||
                StartsWith(orderName, FamilyBbCross) ||
                StartsWith(orderName, FamilyBbReversion) ||
                StartsWith(orderName, FamilyBigTradeFollower);
        }

        private bool IsStopLossOrder(string orderName)
        {
            return orderName != null &&
                orderName.IndexOf("Stop loss", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsProfitTargetOrder(string orderName)
        {
            return orderName != null &&
                orderName.IndexOf("Profit target", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool StartsWith(string value, string prefix)
        {
            return value != null && value.StartsWith(prefix, StringComparison.Ordinal);
        }

        private void ResetBbCrossDeferred()
        {
            bbCrossDeferredDirection = 0;
            bbCrossDeferredStartBar = -1;
        }

        private void ResetBbReversionStopReentry()
        {
            bbReversionStopReentryDirection = 0;
            bbReversionStopReentryBar = -1;
        }

        private void SetBbReversionFirstCondition(int direction, int signalBar)
        {
            if (direction == 1)
                bbReversionLongFirstConditionBar = signalBar;
            else
                bbReversionShortFirstConditionBar = signalBar;
        }

        private void ClearBbReversionFirstCondition(int direction)
        {
            if (direction == 1)
                bbReversionLongFirstConditionBar = -1;
            else
                bbReversionShortFirstConditionBar = -1;
        }

        [NinjaScriptProperty]
        [Display(Name = "Enable Ichimoku Touch", GroupName = "Strategy Switches", Order = 0)]
        public bool EnableIchimokuTouch { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Ichimoku Breakout", GroupName = "Strategy Switches", Order = 1)]
        public bool EnableIchimokuBreakout { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable BB Cross", GroupName = "Strategy Switches", Order = 2)]
        public bool EnableBbCross { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable BB Reversion", GroupName = "Strategy Switches", Order = 3)]
        public bool EnableBbReversion { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable BigTrade Follower", GroupName = "Strategy Switches", Order = 4)]
        public bool EnableBigTradeFollower { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Market Open Time HHmmss", GroupName = "Time", Order = 10)]
        public int MarketOpenTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Market Open Protection", GroupName = "Time", Order = 11)]
        public bool UseMarketOpenProtection { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Trade Start Time HHmmss", GroupName = "Time", Order = 12)]
        public int TradeStartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Trade End Time HHmmss", GroupName = "Time", Order = 13)]
        public int TradeEndTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Exit At Trade End", GroupName = "Time", Order = 14)]
        public bool ExitAtTradeEnd { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BB Base Quantity", GroupName = "Quantity", Order = 20)]
        public int BbBaseQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Ichimoku Quantity Multiplier", GroupName = "Quantity", Order = 21)]
        public int IchimokuQuantityMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Double Reentry Multiplier", GroupName = "Quantity", Order = 22)]
        public int DoubleReentryMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "MACD Fast", GroupName = "Indicators", Order = 30)]
        public int MacdFast { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name = "MACD Slow", GroupName = "Indicators", Order = 31)]
        public int MacdSlow { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "MACD Smooth", GroupName = "Indicators", Order = 32)]
        public int MacdSmooth { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Bollinger Length", GroupName = "Indicators", Order = 33)]
        public int BollingerLength { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Bollinger StdDev", GroupName = "Indicators", Order = 34)]
        public double BollingerStdDev { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Ichimoku Tenkan", GroupName = "Ichimoku", Order = 40)]
        public int IchimokuTenkanPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Ichimoku Kijun", GroupName = "Ichimoku", Order = 41)]
        public int IchimokuKijunPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 300)]
        [Display(Name = "Ichimoku Senkou B", GroupName = "Ichimoku", Order = 42)]
        public int IchimokuSenkouBPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Ichimoku Displacement", GroupName = "Ichimoku", Order = 43)]
        public int IchimokuDisplacement { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Displaced Ichimoku Cloud", GroupName = "Ichimoku", Order = 44)]
        public bool UseDisplacedIchimokuCloud { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Touch Lookback Bars", GroupName = "Rules", Order = 50)]
        public int TouchLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Ichimoku Touch Buffer Ticks", GroupName = "Rules", Order = 51)]
        public int IchimokuTouchBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Breakout MACD Lookback Bars", GroupName = "Rules", Order = 52)]
        public int BreakoutMacdLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Breakout Bias Direction", GroupName = "Rules", Order = 53)]
        public bool RequireBreakoutBiasDirection { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Volatility Lookback Bars", GroupName = "Risk", Order = 60)]
        public int VolatilityLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Ichimoku Stop Range Multiplier", GroupName = "Risk", Order = 61)]
        public double IchimokuStopRangeMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "BB Stop Range Multiplier", GroupName = "Risk", Order = 62)]
        public double BbStopRangeMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name = "Time Exit Minutes", GroupName = "Risk", Order = 63)]
        public int TimeExitMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BB Cross Cloud Lookback Bars", GroupName = "BB Cross", Order = 70)]
        public int BbCrossCloudLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "BB Cross Prior Reversion Bars", GroupName = "BB Cross", Order = 71)]
        public int BbCrossPriorReversionBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "BB Cross Deferred Min Bars", GroupName = "BB Cross", Order = 72)]
        public int BbCrossDeferredMinBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BB Cross Deferred Max Bars", GroupName = "BB Cross", Order = 73)]
        public int BbCrossDeferredMaxBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BB Cross Loss Reentry Bars", GroupName = "BB Cross", Order = 74)]
        public int BbCrossLossReentryBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "BB Cross Double Loss Block Minutes", GroupName = "BB Cross", Order = 75)]
        public int BbCrossDoubleLossBlockMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "BB Cross Middle Close Tolerance Points", GroupName = "BB Cross", Order = 76)]
        public double BbCrossMiddleCloseTolerancePoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "BB Cross Stop Range Multiplier", GroupName = "BB Cross", Order = 77)]
        public double BbCrossStopRangeMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "BB Reversion Retest Min Bars", GroupName = "BB Reversion", Order = 80)]
        public int BbReversionRetestMinBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BB Reversion Retest Max Bars", GroupName = "BB Reversion", Order = 81)]
        public int BbReversionRetestMaxBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "BB Reversion Cooldown Minutes", GroupName = "BB Reversion", Order = 82)]
        public int BbReversionCooldownMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BB Reversion Loss Bypass Bars", GroupName = "BB Reversion", Order = 84)]
        public int BbReversionLossBypassBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "BB Pair Daily Loss Limit", GroupName = "BB Pair Risk", Order = 85)]
        public int BbPairDailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name = "BB Pair Daily Loss Block Minutes", GroupName = "BB Pair Risk", Order = 86)]
        public int BbPairDailyLossBlockMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "BigTrade Volume", GroupName = "Big Trade", Order = 100)]
        public int BigTradeVolume { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BigTrade Quantity", GroupName = "Big Trade", Order = 101)]
        public int BigTradeQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "BigTrade Scale Out Quantity", GroupName = "Big Trade", Order = 102)]
        public int BigTradeScaleOutQuantity { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "BigTrade First Target Ticks", GroupName = "Big Trade", Order = 103)]
        public int BigTradeProfitTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 2000)]
        [Display(Name = "BigTrade Runner Target Ticks", GroupName = "Big Trade", Order = 104)]
        public int BigTradeRunnerTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "BigTrade Initial Stop Ticks", GroupName = "Big Trade", Order = 105)]
        public int BigTradeInitialStopTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BigTrade Use ATR Stop", GroupName = "Big Trade", Order = 106)]
        public bool BigTradeUseAtrStopSizing { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "BigTrade ATR Period", GroupName = "Big Trade", Order = 107)]
        public int BigTradeAtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "BigTrade ATR Stop Multiplier", GroupName = "Big Trade", Order = 108)]
        public double BigTradeAtrStopMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "BigTrade Min Stop Ticks", GroupName = "Big Trade", Order = 109)]
        public int BigTradeMinStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 2000)]
        [Display(Name = "BigTrade Max Stop Ticks", GroupName = "Big Trade", Order = 110)]
        public int BigTradeMaxStopTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BigTrade Move Runner BE", GroupName = "Big Trade", Order = 111)]
        public bool BigTradeMoveRunnerToBreakeven { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "BigTrade BE Plus Ticks", GroupName = "Big Trade", Order = 112)]
        public int BigTradeBreakevenPlusTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BigTrade Use Daily Loss Limit", GroupName = "Big Trade", Order = 113)]
        public bool BigTradeUseDailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BigTrade Max Daily Losses", GroupName = "Big Trade", Order = 114)]
        public int BigTradeMaxDailyLosingTrades { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1)]
        [Display(Name = "BigTrade Direction Mode 0=BidAsk 1=TickRule", GroupName = "Big Trade", Order = 115)]
        public int BigTradeDirectionMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BigTrade Ignore Inside Spread", GroupName = "Big Trade", Order = 116)]
        public bool BigTradeIgnoreInsideSpread { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BigTrade Use EMA/VWAP Filter", GroupName = "Big Trade", Order = 117)]
        public bool BigTradeUseEmaVwapFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "BigTrade EMA Period", GroupName = "Big Trade", Order = 118)]
        public int BigTradeEmaFilterPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BigTrade Use EMA Slope", GroupName = "Big Trade", Order = 119)]
        public bool BigTradeUseEmaSlopeFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "BigTrade EMA Slope Lookback", GroupName = "Big Trade", Order = 120)]
        public int BigTradeEmaSlopeLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "BigTrade Min EMA Slope Ticks", GroupName = "Big Trade", Order = 121)]
        public int BigTradeMinEmaSlopeTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BigTrade Use ADX Filter", GroupName = "Big Trade", Order = 122)]
        public bool BigTradeUseAdxFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BigTrade ADX Period", GroupName = "Big Trade", Order = 123)]
        public int BigTradeAdxPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "BigTrade Min ADX", GroupName = "Big Trade", Order = 124)]
        public double BigTradeMinAdx { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BigTrade One Signal Per Bar", GroupName = "Big Trade", Order = 125)]
        public bool BigTradeOneSignalPerBar { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Export Performance CSV", GroupName = "Export", Order = 90)]
        public bool ExportPerformanceCsv { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Performance Export Folder", GroupName = "Export", Order = 91)]
        public string PerformanceExportFolder { get; set; }
    }
}
