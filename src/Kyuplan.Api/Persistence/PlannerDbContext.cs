using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Persistence;

/// <summary>
/// The API's own database (SQLite, or PostgreSQL through <see cref="PlannerPostgresDbContext"/>): exams and the filters saved for them (one exam, many filters), belts and what they require,
/// the club's members, and the branding as administrators changed it. An exam is a pointer to the Spond event that is the exam (plus that event's name and day, so that the
/// list still reads when Spond cannot be reached) with who was invited to it. Members and those invitations are copies of what
/// Spond says, kept in line by <see cref="SpondSync"/>; the belts, the belt a member holds (and since when), who is saved for an
/// exam and who received which belt at it are the users' own.
/// </summary>
public class PlannerDbContext : DbContext
{
    public PlannerDbContext(DbContextOptions<PlannerDbContext> options) : base(options)
    {
    }

    /// <summary>For <see cref="PlannerPostgresDbContext"/>.</summary>
    protected PlannerDbContext(DbContextOptions options) : base(options)
    {
    }

    public const int NameLength = 200;
    public const int IdLength = 200;
    public const int TermsLength = 500;
    public const int ColorLength = 7;
    private const int RsvpLength = 20;
    private const int SlotLength = 30;
    private const int ContentTypeLength = 50;
    public const int TokenLength = 64;

    /// <summary>Check that a column holds <c>#RRGGBB</c>: the colours go straight into CSS and WPF brushes, so nothing else may be stored.</summary>
    private static string IsColor(string column, bool postgres) => postgres
        ? $"\"{column}\" ~ '^#[0-9A-Fa-f]{{6}}$'"
        : $"\"{column}\" GLOB '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'";

    // Filter names are unique per exam regardless of case ("filter a" is "Filter A"). Exam names keep the collation they
    // got in the first migration: taking it off would make SQLite rebuild the Exams table, which EF cannot do in a transaction.
    private const string NoCase = PostgresModel.NoCase;

    public DbSet<Exam> Exams => Set<Exam>();

    public DbSet<SavedFilter> SavedFilters => Set<SavedFilter>();

    public DbSet<ExamParticipant> ExamParticipants => Set<ExamParticipant>();

    public DbSet<ExamCandidate> ExamCandidates => Set<ExamCandidate>();

    public DbSet<ExamResult> ExamResults => Set<ExamResult>();

    public DbSet<ExamPrice> ExamPrices => Set<ExamPrice>();

    public DbSet<ExamPayment> ExamPayments => Set<ExamPayment>();

    public DbSet<Belt> Belts => Set<Belt>();

    public DbSet<BeltPrerequisite> BeltPrerequisites => Set<BeltPrerequisite>();

    public DbSet<Member> Members => Set<Member>();

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<CommentEdit> CommentEdits => Set<CommentEdit>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<MemberRating> MemberRatings => Set<MemberRating>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<CommentTag> CommentTags => Set<CommentTag>();

    public DbSet<CalendarFeed> CalendarFeeds => Set<CalendarFeed>();

    public DbSet<ManualGroup> ManualGroups => Set<ManualGroup>();

    public DbSet<ManualSubGroup> ManualSubGroups => Set<ManualSubGroup>();

    public DbSet<ManualMember> ManualMembers => Set<ManualMember>();

    public DbSet<ManualMemberSubGroup> ManualMemberSubGroups => Set<ManualMemberSubGroup>();

    public DbSet<ManualEvent> ManualEvents => Set<ManualEvent>();

    public DbSet<ManualInvitation> ManualInvitations => Set<ManualInvitation>();

    public DbSet<ManualEventTrainer> ManualEventTrainers => Set<ManualEventTrainer>();

    public DbSet<CustomBranding> CustomBranding => Set<CustomBranding>();

    public DbSet<CustomBrandingImage> CustomBrandingImages => Set<CustomBrandingImage>();

    // The member filters reach what belongs to a member through a required navigation; EF warns about the parents of these rows (a reply's parent comment, the exam) that have none.
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var postgres = Database.IsNpgsql();

        modelBuilder.Entity<CustomBranding>(branding =>
        {
            branding.Property(b => b.Id).ValueGeneratedNever();
            branding.Property(b => b.ProductName).HasMaxLength(BrandingRules.MaxProductNameLength);
            branding.Property(b => b.OrganizationName).HasMaxLength(BrandingRules.MaxOrganizationNameLength);
            branding.Property(b => b.AccentColor).HasMaxLength(ColorLength);
            branding.Property(b => b.DefaultWebTheme).HasMaxLength(ThemeRules.MaxLength).HasDefaultValue(ThemeIds.Default);
            branding.Property(b => b.DefaultWindowsTheme).HasMaxLength(ThemeRules.MaxLength).HasDefaultValue(ThemeIds.Default);
            branding.Property(b => b.Currency).HasMaxLength(BrandingRules.MaxCurrencyLength).HasDefaultValue(BrandingRules.DefaultCurrency);

            branding.ToTable(table => table.HasCheckConstraint("CK_CustomBranding_Single", "\"Id\" = 1"));
        });

        modelBuilder.Entity<CustomBrandingImage>(image =>
        {
            image.HasKey(i => i.Slot);
            image.Property(i => i.Slot).HasMaxLength(SlotLength);
            image.Property(i => i.ContentType).HasMaxLength(ContentTypeLength);

            image.ToTable(table => table.HasCheckConstraint("CK_CustomBrandingImages_ContentAndType", "(\"Content\" IS NULL) = (\"ContentType\" IS NULL)"));
        });

        modelBuilder.Entity<Exam>(exam =>
        {
            exam.Property(e => e.GroupId).HasMaxLength(IdLength);
            exam.Property(e => e.EventId).HasMaxLength(IdLength);
            exam.Property(e => e.Name).HasMaxLength(NameLength).UseCollation(NoCase);
            exam.HasIndex(e => e.EventId).IsUnique();
        });

        modelBuilder.Entity<SavedFilter>(filter =>
        {
            filter.Property(f => f.Name).HasMaxLength(NameLength).UseCollation(NoCase);
            filter.Property(f => f.GroupId).HasMaxLength(IdLength);
            filter.Property(f => f.FromEventId).HasMaxLength(IdLength);
            filter.Property(f => f.ToEventId).HasMaxLength(IdLength);
            filter.Property(f => f.EventNameContains).HasMaxLength(TermsLength);
            filter.Property(f => f.EventNameExcludes).HasMaxLength(TermsLength);

            filter.HasOne(f => f.Exam).WithMany(e => e.Filters).HasForeignKey(f => f.ExamId).OnDelete(DeleteBehavior.Cascade);
            filter.HasIndex(f => new { f.ExamId, f.Name }).IsUnique();
        });

        modelBuilder.Entity<Belt>(belt =>
        {
            belt.Property(b => b.Name).HasMaxLength(NameLength).UseCollation(NoCase);
            belt.Property(b => b.PrimaryColor).HasMaxLength(ColorLength);
            belt.Property(b => b.SecondaryColor).HasMaxLength(ColorLength);
            belt.HasIndex(b => b.Name).IsUnique();

            belt.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Belts_PrimaryColor", IsColor("PrimaryColor", postgres));
                table.HasCheckConstraint("CK_Belts_SecondaryColor", $"\"SecondaryColor\" IS NULL OR {IsColor("SecondaryColor", postgres)}");
            });
        });

        modelBuilder.Entity<BeltPrerequisite>(prerequisite =>
        {
            prerequisite.HasKey(p => new { p.BeltId, p.RequiredBeltId });

            // A belt takes its prerequisites with it; a belt that is somebody's prerequisite cannot be deleted.
            prerequisite.HasOne(p => p.Belt).WithMany(b => b.Prerequisites).HasForeignKey(p => p.BeltId).OnDelete(DeleteBehavior.Cascade);
            prerequisite.HasOne(p => p.RequiredBelt).WithMany().HasForeignKey(p => p.RequiredBeltId).OnDelete(DeleteBehavior.Restrict);

            prerequisite.ToTable(table =>
            {
                table.HasCheckConstraint("CK_BeltPrerequisites_NotItself", "\"BeltId\" <> \"RequiredBeltId\"");
                table.HasCheckConstraint("CK_BeltPrerequisites_MinAgeYears", "\"MinAgeYears\" >= 0");
                table.HasCheckConstraint("CK_BeltPrerequisites_MinWaitMonths", "\"MinWaitMonths\" >= 0");
            });
        });

        modelBuilder.Entity<Member>(member =>
        {
            member.Property(m => m.GroupId).HasMaxLength(IdLength);
            member.Property(m => m.SpondId).HasMaxLength(IdLength);
            member.Property(m => m.Name).HasMaxLength(NameLength);
            member.HasIndex(m => new { m.GroupId, m.SpondId }).IsUnique();
            member.HasIndex(m => m.UserId); // not unique: a user may be several members (one per Spond group)

            // A belt that somebody holds cannot be deleted.
            member.HasOne(m => m.Belt).WithMany().HasForeignKey(m => m.BeltId).OnDelete(DeleteBehavior.Restrict);
        });

        // A member that is gone from the data source is hidden, with everything about them, until the retention is over (SpondSync;
        // it and the backup see them with IgnoreQueryFilters), and so is a member a user hid (Member.Hidden; MemberService lists them for
        // the page of hidden members). The filters of the rows that belong to a member say the same, so that no payment, comment or
        // result of a hidden member turns up without the member.
        modelBuilder.Entity<Member>().HasQueryFilter(m => m.RemovedAt == null && !m.Hidden);
        modelBuilder.Entity<ExamParticipant>().HasQueryFilter(p => p.Member.RemovedAt == null && !p.Member.Hidden);
        modelBuilder.Entity<ExamCandidate>().HasQueryFilter(c => c.Member.RemovedAt == null && !c.Member.Hidden);
        modelBuilder.Entity<ExamResult>().HasQueryFilter(r => r.Member.RemovedAt == null && !r.Member.Hidden);
        modelBuilder.Entity<ExamPayment>().HasQueryFilter(p => p.Member.RemovedAt == null && !p.Member.Hidden);
        modelBuilder.Entity<Comment>().HasQueryFilter(c => c.Member.RemovedAt == null && !c.Member.Hidden);
        modelBuilder.Entity<MemberRating>().HasQueryFilter(r => r.Member.RemovedAt == null && !r.Member.Hidden);
        modelBuilder.Entity<CommentEdit>().HasQueryFilter(e => e.Comment.Member.RemovedAt == null && !e.Comment.Member.Hidden);
        modelBuilder.Entity<CommentTag>().HasQueryFilter(t => t.Comment.Member.RemovedAt == null && !t.Comment.Member.Hidden);

        modelBuilder.Entity<ManualGroup>(group =>
        {
            group.Property(g => g.Id).HasMaxLength(IdLength);
            group.Property(g => g.Name).HasMaxLength(NameLength).UseCollation(NoCase);
            group.HasIndex(g => g.Name).IsUnique();
        });

        modelBuilder.Entity<ManualSubGroup>(subGroup =>
        {
            subGroup.Property(s => s.Id).HasMaxLength(IdLength);
            subGroup.Property(s => s.GroupId).HasMaxLength(IdLength);
            subGroup.Property(s => s.Name).HasMaxLength(NameLength).UseCollation(NoCase);
            subGroup.HasIndex(s => new { s.GroupId, s.Name }).IsUnique();
            subGroup.HasOne(s => s.Group).WithMany(g => g.SubGroups).HasForeignKey(s => s.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ManualMember>(member =>
        {
            member.Property(m => m.Id).HasMaxLength(IdLength);
            member.Property(m => m.GroupId).HasMaxLength(IdLength);
            member.Property(m => m.Name).HasMaxLength(NameLength);
            member.HasOne(m => m.Group).WithMany(g => g.Members).HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ManualMemberSubGroup>(link =>
        {
            link.HasKey(l => new { l.MemberId, l.SubGroupId });
            link.Property(l => l.MemberId).HasMaxLength(IdLength);
            link.Property(l => l.SubGroupId).HasMaxLength(IdLength);
            link.HasOne(l => l.Member).WithMany(m => m.SubGroups).HasForeignKey(l => l.MemberId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne(l => l.SubGroup).WithMany().HasForeignKey(l => l.SubGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ManualEvent>(manualEvent =>
        {
            manualEvent.Property(e => e.Id).HasMaxLength(IdLength);
            manualEvent.Property(e => e.GroupId).HasMaxLength(IdLength);
            manualEvent.Property(e => e.Name).HasMaxLength(NameLength);
            manualEvent.HasOne(e => e.Group).WithMany(g => g.Events).HasForeignKey(e => e.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ManualInvitation>(invitation =>
        {
            invitation.HasKey(i => new { i.EventId, i.MemberId });
            invitation.Property(i => i.EventId).HasMaxLength(IdLength);
            invitation.Property(i => i.MemberId).HasMaxLength(IdLength);
            invitation.Property(i => i.Rsvp).HasConversion<string>().HasMaxLength(RsvpLength);
            invitation.HasOne(i => i.Event).WithMany(e => e.Invitations).HasForeignKey(i => i.EventId).OnDelete(DeleteBehavior.Cascade);
            invitation.HasOne(i => i.Member).WithMany().HasForeignKey(i => i.MemberId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ManualEventTrainer>(trainer =>
        {
            trainer.HasKey(t => new { t.EventId, t.MemberId });
            trainer.Property(t => t.EventId).HasMaxLength(IdLength);
            trainer.Property(t => t.MemberId).HasMaxLength(IdLength);
            trainer.HasOne(t => t.Event).WithMany(e => e.Trainers).HasForeignKey(t => t.EventId).OnDelete(DeleteBehavior.Cascade);
            trainer.HasOne(t => t.Member).WithMany().HasForeignKey(t => t.MemberId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExamParticipant>(participant =>
        {
            participant.HasKey(p => new { p.ExamId, p.MemberId });
            participant.Property(p => p.Rsvp).HasConversion<string>().HasMaxLength(RsvpLength);
            participant.Ignore(p => p.Attended);

            participant.HasOne(p => p.Exam).WithMany(e => e.Participants).HasForeignKey(p => p.ExamId).OnDelete(DeleteBehavior.Cascade);
            participant.HasOne(p => p.Member).WithMany(m => m.Participations).HasForeignKey(p => p.MemberId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExamCandidate>(candidate =>
        {
            candidate.HasKey(c => new { c.ExamId, c.MemberId });

            candidate.HasOne(c => c.Exam).WithMany(e => e.Candidates).HasForeignKey(c => c.ExamId).OnDelete(DeleteBehavior.Cascade);
            candidate.HasOne(c => c.Member).WithMany().HasForeignKey(c => c.MemberId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExamResult>(result =>
        {
            result.HasKey(r => new { r.ExamId, r.MemberId });

            result.HasOne(r => r.Exam).WithMany(e => e.Results).HasForeignKey(r => r.ExamId).OnDelete(DeleteBehavior.Cascade);
            result.HasOne(r => r.Member).WithMany().HasForeignKey(r => r.MemberId).OnDelete(DeleteBehavior.Cascade);

            // A belt that was handed out at an exam cannot be deleted.
            result.HasOne(r => r.Belt).WithMany().HasForeignKey(r => r.BeltId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExamPrice>(price =>
        {
            price.Property(p => p.Amount).HasPrecision(10, 2);
            price.Property(p => p.BeltPrice).HasPrecision(10, 2);
            price.HasIndex(p => p.BeltId).IsUnique(); // the general fee (null) is kept to one row by ExamPaymentService

            // The belt's costs go with it.
            price.HasOne(p => p.Belt).WithMany().HasForeignKey(p => p.BeltId).OnDelete(DeleteBehavior.Cascade);
            price.ToTable(table => table.HasCheckConstraint("CK_ExamPrices_Amount", "\"Amount\" >= 0 AND \"BeltPrice\" >= 0"));
        });

        modelBuilder.Entity<ExamPayment>(payment =>
        {
            payment.HasKey(p => new { p.ExamId, p.MemberId });
            payment.Property(p => p.AmountDue).HasPrecision(10, 2);
            payment.Property(p => p.StampFee).HasPrecision(10, 2);
            payment.Property(p => p.BeltPrice).HasPrecision(10, 2);
            payment.Property(p => p.AmountPaid).HasPrecision(10, 2);
            payment.Property(p => p.Note).HasMaxLength(PaymentRules.MaxNoteLength);

            payment.HasOne(p => p.Exam).WithMany(e => e.Payments).HasForeignKey(p => p.ExamId).OnDelete(DeleteBehavior.Cascade);
            payment.HasOne(p => p.Member).WithMany().HasForeignKey(p => p.MemberId).OnDelete(DeleteBehavior.Cascade);
            payment.ToTable(table => table.HasCheckConstraint("CK_ExamPayments_Amounts", "\"AmountDue\" >= 0 AND \"AmountPaid\" >= 0"));
        });

        modelBuilder.Entity<Comment>(comment =>
        {
            comment.Property(c => c.Body).HasMaxLength(CommentRules.MaxBodyLength);
            comment.Property(c => c.AuthorName).HasMaxLength(AccountRules.MaxUserNameLength);

            // A member's comments go with the member; the replies of a comment go with it.
            comment.HasOne(c => c.Member).WithMany(m => m.Comments).HasForeignKey(c => c.MemberId).OnDelete(DeleteBehavior.Cascade);
            comment.HasOne(c => c.Parent).WithMany(c => c.Replies).HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommentEdit>(edit =>
        {
            edit.Property(e => e.EditorName).HasMaxLength(AccountRules.MaxUserNameLength);
            edit.Property(e => e.PreviousBody).HasMaxLength(CommentRules.MaxBodyLength);

            edit.HasOne(e => e.Comment).WithMany(c => c.Edits).HasForeignKey(e => e.CommentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEntry>(entry =>
        {
            entry.Property(e => e.UserName).HasMaxLength(AccountRules.MaxUserNameLength);
            entry.Property(e => e.Area).HasMaxLength(ContentTypeLength);
            entry.Property(e => e.Action).HasMaxLength(ContentTypeLength);
            entry.Property(e => e.Target).HasMaxLength(NameLength);
            entry.HasIndex(e => new { e.Area, e.Id });
            entry.HasIndex(e => new { e.UserId, e.Id });
        });

        modelBuilder.Entity<MemberRating>(rating =>
        {
            rating.Property(r => r.EventId).HasMaxLength(IdLength);
            rating.Property(r => r.UserName).HasMaxLength(AccountRules.MaxUserNameLength);

            // One evaluation per user, member and event; a member's evaluations go with the member.
            rating.HasIndex(r => new { r.MemberId, r.EventId, r.UserId }).IsUnique();
            rating.HasIndex(r => r.EventId);
            rating.HasOne(r => r.Member).WithMany(m => m.Ratings).HasForeignKey(r => r.MemberId).OnDelete(DeleteBehavior.Cascade);
            rating.ToTable(table => table.HasCheckConstraint("CK_MemberRatings_Stars", "\"Stars\" BETWEEN 0 AND 5"));
        });

        modelBuilder.Entity<Tag>(tag =>
        {
            tag.Property(t => t.Name).HasMaxLength(TagRules.MaxNameLength).UseCollation(NoCase);
            tag.Property(t => t.EventGroupId).HasMaxLength(IdLength);
            tag.Property(t => t.EventId).HasMaxLength(IdLength);
            tag.Property(t => t.EventName).HasMaxLength(NameLength);
            tag.HasIndex(t => t.Name).IsUnique();

            // A tag is about one event or none: what is known of the event is all there or all missing.
            tag.ToTable(table => table.HasCheckConstraint(
                "CK_Tags_Event",
                "(\"EventId\" IS NULL AND \"EventGroupId\" IS NULL AND \"EventName\" IS NULL AND \"EventStart\" IS NULL) " +
                "OR (\"EventId\" IS NOT NULL AND \"EventGroupId\" IS NOT NULL AND \"EventName\" IS NOT NULL AND \"EventStart\" IS NOT NULL)"));
        });

        modelBuilder.Entity<CalendarFeed>(feed =>
        {
            feed.Property(f => f.Name).HasMaxLength(CalendarFeedRules.MaxNameLength).UseCollation(NoCase);
            feed.Property(f => f.Token).HasMaxLength(TokenLength);
            feed.HasIndex(f => f.Name).IsUnique();
            feed.HasIndex(f => f.Token).IsUnique();
        });

        modelBuilder.Entity<CommentTag>(commentTag =>
        {
            commentTag.HasKey(t => new { t.CommentId, t.TagId });

            // A tag that is deleted is taken off the comments; a comment that is deleted lets go of its tags.
            commentTag.HasOne(t => t.Comment).WithMany(c => c.Tags).HasForeignKey(t => t.CommentId).OnDelete(DeleteBehavior.Cascade);
            commentTag.HasOne(t => t.Tag).WithMany(t => t.Comments).HasForeignKey(t => t.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        if (postgres) PostgresModel.Adapt(modelBuilder);
    }
}

/// <summary>
/// <see cref="PlannerDbContext"/> on PostgreSQL: the same model, with migrations of its own (<c>Persistence/PostgresMigrations</c>), because
/// the SQLite ones are SQL for SQLite.
/// </summary>
public sealed class PlannerPostgresDbContext(DbContextOptions<PlannerPostgresDbContext> options) : PlannerDbContext(options);
