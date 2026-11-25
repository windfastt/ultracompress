namespace UltraCompress.Core.Search;

public enum TournamentStopReason
{
    WinnerFound,
    SuccessiveHalvingComplete,
    IncompressibleBypass,
    TimeBudgetExceeded,
    UcbEarlyStop,
    DiminishingReturns,
    AllArmsExhausted,
    SkippedByAdvisor,
}
