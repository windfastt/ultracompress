namespace UltraCompress.Core.Search;

public enum TournamentStopReason
{
    WinnerFound,
    TimeBudgetExceeded,
    UcbEarlyStop,
    DiminishingReturns,
    AllArmsExhausted,
    SkippedByAdvisor,
}