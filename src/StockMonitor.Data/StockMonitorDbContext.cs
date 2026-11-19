using Microsoft.EntityFrameworkCore;
using StockMonitor.Data.Entities;

namespace StockMonitor.Data;

/// <summary>
/// Database-first mapping: the schema is owned by the SQL scripts in /database (DBA-reviewable, idempotent),
/// so EF is used for querying and simple writes only - no EF migrations.
/// </summary>
public class StockMonitorDbContext : DbContext
{
    public StockMonitorDbContext(DbContextOptions<StockMonitorDbContext> options) : base(options) { }

    public DbSet<BatchLog> BatchLogs => Set<BatchLog>();
    public DbSet<DataQualityIssue> DataQualityIssues => Set<DataQualityIssue>();
    public DbSet<CleanTransactionRow> CleanTransactions => Set<CleanTransactionRow>();
    public DbSet<ReplayQueueItem> ReplayQueue => Set<ReplayQueueItem>();

    public DbSet<DimAccount> Accounts => Set<DimAccount>();
    public DbSet<DimSecurity> Securities => Set<DimSecurity>();
    public DbSet<DimTransactionType> TransactionTypes => Set<DimTransactionType>();
    public DbSet<FactTransaction> Transactions => Set<FactTransaction>();
    public DbSet<CorporateAction> CorporateActions => Set<CorporateAction>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<FactRealizedGain> RealizedGains => Set<FactRealizedGain>();

    public DbSet<SecurityPrice> Prices => Set<SecurityPrice>();
    public DbSet<PriceTick> PriceTicks => Set<PriceTick>();
    public DbSet<PortfolioSnapshot> Snapshots => Set<PortfolioSnapshot>();
    public DbSet<Alert> Alerts => Set<Alert>();

    public DbSet<VwTransactionDetail> TransactionDetails => Set<VwTransactionDetail>();
    public DbSet<VwMonthlyCashFlow> MonthlyCashFlows => Set<VwMonthlyCashFlow>();
    public DbSet<VwDividendBySecurity> DividendsBySecurity => Set<VwDividendBySecurity>();
    public DbSet<VwCashBalance> CashBalances => Set<VwCashBalance>();
    public DbSet<VwRealizedGainByTaxYear> RealizedByTaxYear => Set<VwRealizedGainByTaxYear>();
    public DbSet<VwIsaAllowance> IsaAllowance => Set<VwIsaAllowance>();
    public DbSet<VwHolding> Holdings => Set<VwHolding>();
    public DbSet<VwBatchSummary> BatchSummaries => Set<VwBatchSummary>();

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // Default money precision mirrors the DDL; quantities/prices override per property below.
        c.Properties<decimal>().HavePrecision(19, 4);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BatchLog>(e => { e.ToTable("BatchLog", "etl"); e.HasKey(x => x.BatchId); });
        b.Entity<DataQualityIssue>(e => { e.ToTable("DataQualityIssue", "etl"); e.HasKey(x => x.IssueId); });

        b.Entity<CleanTransactionRow>(e =>
        {
            e.ToTable("CleanTransaction", "stg");
            e.HasKey(x => x.CleanId);
            e.Property(x => x.Shares).HasPrecision(28, 10);
            e.Property(x => x.PriceLocal).HasPrecision(19, 6);
            e.Property(x => x.PriceGbp).HasPrecision(19, 6);
            e.Property(x => x.FxRate).HasPrecision(19, 8);
        });
        b.Entity<ReplayQueueItem>(e => { e.ToTable("ReplayQueue", "stg"); e.HasKey(x => x.ReplayId); });

        b.Entity<DimAccount>(e => { e.ToTable("DimAccount", "dw"); e.HasKey(x => x.AccountKey); });
        b.Entity<DimSecurity>(e => { e.ToTable("DimSecurity", "dw"); e.HasKey(x => x.SecurityKey); });
        b.Entity<DimTransactionType>(e => { e.ToTable("DimTransactionType", "dw"); e.HasKey(x => x.TransactionTypeKey); });

        b.Entity<FactTransaction>(e =>
        {
            e.ToTable("FactTransaction", "dw");
            e.HasKey(x => x.TransactionId);
            e.Property(x => x.Shares).HasPrecision(28, 10);
            e.Property(x => x.PriceLocal).HasPrecision(19, 6);
            e.Property(x => x.PriceGbp).HasPrecision(19, 6);
            e.Property(x => x.FxRate).HasPrecision(19, 8);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountKey);
            e.HasOne(x => x.Security).WithMany().HasForeignKey(x => x.SecurityKey);
            e.HasOne(x => x.TransactionType).WithMany().HasForeignKey(x => x.TransactionTypeKey);
        });

        b.Entity<CorporateAction>(e =>
        {
            e.ToTable("CorporateAction", "dw");
            e.HasKey(x => x.CorporateActionId);
            e.Property(x => x.Ratio).HasPrecision(19, 8);
            e.Property(x => x.CostBasisAllocation).HasPrecision(9, 6);
        });

        b.Entity<Position>(e =>
        {
            e.ToTable("Position", "dw");
            e.HasKey(x => new { x.AccountKey, x.SecurityKey });
            e.Property(x => x.Quantity).HasPrecision(28, 10);
            e.Property(x => x.AvgCostGbp).HasPrecision(19, 6);
        });

        b.Entity<FactRealizedGain>(e =>
        {
            e.ToTable("FactRealizedGain", "dw");
            e.HasKey(x => x.RealizedGainId);
            e.Property(x => x.SharesDisposed).HasPrecision(28, 10);
        });

        b.Entity<SecurityPrice>(e =>
        {
            e.ToTable("SecurityPrice", "rt");
            e.HasKey(x => x.SecurityKey);
            e.Property(x => x.SecurityKey).ValueGeneratedNever();
            e.Property(x => x.PriceGbp).HasPrecision(19, 6);
            e.Property(x => x.PrevCloseGbp).HasPrecision(19, 6);
        });
        b.Entity<PriceTick>(e => { e.ToTable("PriceTick", "rt"); e.HasKey(x => x.TickId); e.Property(x => x.PriceGbp).HasPrecision(19, 6); });
        b.Entity<PortfolioSnapshot>(e => { e.ToTable("PortfolioSnapshot", "rt"); e.HasKey(x => x.SnapshotId); });
        b.Entity<Alert>(e => { e.ToTable("Alert", "rt"); e.HasKey(x => x.AlertId); });

        b.Entity<VwTransactionDetail>(e => { e.HasNoKey(); e.ToView("vw_TransactionDetail", "dw"); });
        b.Entity<VwMonthlyCashFlow>(e => { e.HasNoKey(); e.ToView("vw_MonthlyCashFlow", "dw"); });
        b.Entity<VwDividendBySecurity>(e => { e.HasNoKey(); e.ToView("vw_DividendBySecurity", "dw"); });
        b.Entity<VwCashBalance>(e => { e.HasNoKey(); e.ToView("vw_CashBalance", "dw"); });
        b.Entity<VwRealizedGainByTaxYear>(e => { e.HasNoKey(); e.ToView("vw_RealizedGainByTaxYear", "dw"); });
        b.Entity<VwIsaAllowance>(e => { e.HasNoKey(); e.ToView("vw_IsaAllowance", "dw"); });
        b.Entity<VwHolding>(e => { e.HasNoKey(); e.ToView("vw_Holding", "dw"); });
        b.Entity<VwBatchSummary>(e => { e.HasNoKey(); e.ToView("vw_BatchSummary", "etl"); });
    }
}
