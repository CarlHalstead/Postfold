using Microsoft.EntityFrameworkCore;
using Postfold.Storage.Models.Persistence;

namespace Postfold.Storage.Configurations;

internal static class ProfileConfiguration
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountsRow>(entity =>
        {
            entity.ToTable("Accounts", table =>
            {
                table.HasCheckConstraint("Accounts_AccountId", "AccountId > 0");
                table.HasCheckConstraint("Accounts_Active", "Active IN (0,1)");
            });
            entity.HasKey("AccountId");
            entity.Property(row => row.AccountId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.ProviderKind).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.DisplayName).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.Configuration).HasColumnOrder(3).ValueGeneratedNever().HasDefaultValueSql("'{}'");
            entity.Property(row => row.Capabilities).HasColumnOrder(4).ValueGeneratedNever().HasDefaultValueSql("'{}'");
            entity.Property(row => row.CredentialReference).HasColumnOrder(5).ValueGeneratedNever().HasDefaultValueSql("''");
            entity.Property(row => row.Active).HasColumnOrder(6).ValueGeneratedNever().HasDefaultValueSql("1");
        });

        modelBuilder.Entity<IdentitiesRow>(entity =>
        {
            entity.ToTable("Identities", table =>
            {
                table.HasCheckConstraint("Identities_IsPrimary", "IsPrimary IN (0,1)");
            });
            entity.HasKey("IdentityId");
            entity.HasAlternateKey("AccountId", "IdentityId");
            entity.Property(row => row.IdentityId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.AccountId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.Address).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.DisplayName).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.Signature).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.IsPrimary).HasColumnOrder(5).ValueGeneratedNever();
            entity.HasOne<AccountsRow>().WithMany().HasForeignKey("AccountId").HasPrincipalKey("AccountId").OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex("AccountId").HasDatabaseName("IdentityPrimary").IsUnique().HasFilter("IsPrimary=1");
        });

        modelBuilder.Entity<CollectionsRow>(entity =>
        {
            entity.ToTable("Collections");
            entity.HasKey("FolderId");
            entity.HasAlternateKey("AccountId", "RemoteKey");
            entity.HasAlternateKey("AccountId", "FolderId");
            entity.Property(row => row.FolderId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.AccountId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.RemoteKey).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.ParentId).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.Name).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.Role).HasColumnOrder(5).ValueGeneratedNever();
            entity.Property(row => row.Kind).HasColumnOrder(6).ValueGeneratedNever().HasDefaultValueSql("'Mailbox'");
            entity.Property(row => row.Selectable).HasColumnOrder(7).ValueGeneratedNever();
            entity.Property(row => row.Writable).HasColumnOrder(8).ValueGeneratedNever();
            entity.Property(row => row.UidValidity).HasColumnOrder(9).ValueGeneratedNever();
            entity.HasOne<CollectionsRow>().WithMany().HasForeignKey("AccountId", "ParentId").HasPrincipalKey("AccountId", "FolderId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<AccountsRow>().WithMany().HasForeignKey("AccountId").HasPrincipalKey("AccountId").OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<MessagesRow>(entity =>
        {
            entity.ToTable("Messages", table =>
            {
                table.HasCheckConstraint("Messages_Size", "Size>=0");
            });
            entity.HasKey("MessageId");
            entity.HasAlternateKey("AccountId", "ProviderKey");
            entity.HasAlternateKey("AccountId", "MessageId");
            entity.Property(row => row.MessageId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.AccountId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.ProviderKey).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.MessageIdHeader).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.Subject).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.Participants).HasColumnOrder(5).ValueGeneratedNever();
            entity.Property(row => row.Date).HasColumnOrder(6).ValueGeneratedNever();
            entity.Property(row => row.Size).HasColumnOrder(7).ValueGeneratedNever();
            entity.Property(row => row.IsRead).HasColumnOrder(8).ValueGeneratedNever();
            entity.Property(row => row.Flagged).HasColumnOrder(9).ValueGeneratedNever();
            entity.Property(row => row.Availability).HasColumnOrder(10).ValueGeneratedNever();
            entity.Property(row => row.Pending).HasColumnOrder(11).ValueGeneratedNever().HasDefaultValueSql("0");
            entity.Property(row => row.NativeThreadId).HasColumnOrder(12).ValueGeneratedNever();
            entity.HasOne<AccountsRow>().WithMany().HasForeignKey("AccountId").HasPrincipalKey("AccountId").OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex("AccountId", "Date", "MessageId").HasDatabaseName("MessageDate").IsDescending(false, true, true);
            entity.HasIndex("Date", "MessageId").HasDatabaseName("UnifiedMessageDate").IsDescending(true, true);
        });

        modelBuilder.Entity<MembershipsRow>(entity =>
        {
            entity.ToTable("Memberships");
            entity.HasKey("AccountId", "MessageId", "FolderId");
            entity.Property(row => row.AccountId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.MessageId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.FolderId).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.SeenGeneration).HasColumnOrder(3).ValueGeneratedNever();
            entity.HasOne<MessagesRow>().WithMany().HasForeignKey("AccountId", "MessageId").HasPrincipalKey("AccountId", "MessageId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<CollectionsRow>().WithMany().HasForeignKey("AccountId", "FolderId").HasPrincipalKey("AccountId", "FolderId").OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex("AccountId", "FolderId", "MessageId").HasDatabaseName("FolderMessages");
        });

        modelBuilder.Entity<ImapInstancesRow>(entity =>
        {
            entity.ToTable("ImapInstances", table =>
            {
                table.HasCheckConstraint("ImapInstances_Uid", "Uid>0");
            });
            entity.HasKey("AccountId", "FolderId", "UidValidity", "Uid");
            entity.Property(row => row.AccountId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.FolderId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.UidValidity).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.Uid).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.MessageId).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.ModSeq).HasColumnOrder(5).ValueGeneratedNever();
            entity.HasOne<MessagesRow>().WithMany().HasForeignKey("AccountId", "MessageId").HasPrincipalKey("AccountId", "MessageId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<CollectionsRow>().WithMany().HasForeignKey("AccountId", "FolderId").HasPrincipalKey("AccountId", "FolderId").OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex("AccountId", "MessageId").HasDatabaseName("ImapMessage");
        });

        modelBuilder.Entity<BlobsRow>(entity =>
        {
            entity.ToTable("Blobs", table =>
            {
                table.HasCheckConstraint("Blobs_Length", "Length>=0");
                table.HasCheckConstraint("Blobs_Protected", "Protected IN (0,1)");
            });
            entity.HasKey("BlobId");
            entity.HasAlternateKey("AccountId", "BlobId");
            entity.HasAlternateKey("ObjectName");
            entity.Property(row => row.BlobId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.AccountId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.ObjectName).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.Length).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.Hash).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.Protected).HasColumnOrder(5).ValueGeneratedNever();
            entity.Property(row => row.LastAccess).HasColumnOrder(6).ValueGeneratedNever();
            entity.HasOne<AccountsRow>().WithMany().HasForeignKey("AccountId").HasPrincipalKey("AccountId").OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<BodiesRow>(entity =>
        {
            entity.ToTable("Bodies");
            entity.HasKey("AccountId", "MessageId");
            entity.Property(row => row.AccountId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.MessageId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.BlobId).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.Format).HasColumnOrder(3).ValueGeneratedNever().HasDefaultValueSql("'Plain'");
            entity.Property(row => row.Text).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.LastAccess).HasColumnOrder(5).ValueGeneratedNever();
            entity.HasOne<MessagesRow>().WithMany().HasForeignKey("AccountId", "MessageId").HasPrincipalKey("AccountId", "MessageId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<BlobsRow>().WithMany().HasForeignKey("AccountId", "BlobId").HasPrincipalKey("AccountId", "BlobId").OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<AttachmentsRow>(entity =>
        {
            entity.ToTable("Attachments");
            entity.HasKey("AttachmentId");
            entity.HasAlternateKey("AccountId", "MessageId", "PartKey");
            entity.Property(row => row.AttachmentId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.AccountId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.MessageId).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.PartKey).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.FileName).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.MediaType).HasColumnOrder(5).ValueGeneratedNever();
            entity.Property(row => row.Size).HasColumnOrder(6).ValueGeneratedNever();
            entity.Property(row => row.BlobId).HasColumnOrder(7).ValueGeneratedNever();
            entity.HasOne<MessagesRow>().WithMany().HasForeignKey("AccountId", "MessageId").HasPrincipalKey("AccountId", "MessageId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<BlobsRow>().WithMany().HasForeignKey("AccountId", "BlobId").HasPrincipalKey("AccountId", "BlobId").OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<SyncStateRow>(entity =>
        {
            entity.ToTable("SyncState");
            entity.HasKey("AccountId", "Scope");
            entity.Property(row => row.AccountId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.Scope).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.Generation).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.Cursor).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.ModSequence).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.Complete).HasColumnOrder(5).ValueGeneratedNever();
            entity.Property(row => row.LastSuccess).HasColumnOrder(6).ValueGeneratedNever();
            entity.HasOne<AccountsRow>().WithMany().HasForeignKey("AccountId").HasPrincipalKey("AccountId").OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<PendingOperationsRow>(entity =>
        {
            entity.ToTable("PendingOperations", table =>
            {
                table.HasCheckConstraint("PendingOperations_PayloadVersion", "PayloadVersion=1");
                table.HasCheckConstraint("PendingOperations_Payload", "json_valid(Payload)");
            });
            entity.HasKey("OperationId");
            entity.Property(row => row.OperationId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.AccountId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.MessageId).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.PayloadVersion).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.Payload).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.State).HasColumnOrder(5).ValueGeneratedNever();
            entity.Property(row => row.Revision).HasColumnOrder(6).ValueGeneratedNever();
            entity.Property(row => row.Attempts).HasColumnOrder(7).ValueGeneratedNever();
            entity.Property(row => row.Due).HasColumnOrder(8).ValueGeneratedNever();
            entity.Property(row => row.Evidence).HasColumnOrder(9).ValueGeneratedNever();
            entity.HasOne<MessagesRow>().WithMany().HasForeignKey("AccountId", "MessageId").HasPrincipalKey("AccountId", "MessageId").OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex("AccountId", "State", "Due").HasDatabaseName("OperationDue");
        });

        modelBuilder.Entity<DraftsRow>(entity =>
        {
            entity.ToTable("Drafts");
            entity.HasKey("DraftId");
            entity.HasAlternateKey("AccountId", "DraftId");
            entity.Property(row => row.DraftId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.AccountId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.IdentityId).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.Revision).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.BaseRevision).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.PushedRevision).HasColumnOrder(5).ValueGeneratedNever();
            entity.Property(row => row.BlobId).HasColumnOrder(6).ValueGeneratedNever();
            entity.Property(row => row.RemoteIdentity).HasColumnOrder(7).ValueGeneratedNever();
            entity.Property(row => row.RemoteVersion).HasColumnOrder(8).ValueGeneratedNever();
            entity.Property(row => row.Conflict).HasColumnOrder(9).ValueGeneratedNever();
            entity.Property(row => row.Held).HasColumnOrder(10).ValueGeneratedNever();
            entity.HasOne<IdentitiesRow>().WithMany().HasForeignKey("AccountId", "IdentityId").HasPrincipalKey("AccountId", "IdentityId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<BlobsRow>().WithMany().HasForeignKey("AccountId", "BlobId").HasPrincipalKey("AccountId", "BlobId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<AccountsRow>().WithMany().HasForeignKey("AccountId").HasPrincipalKey("AccountId").OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<DraftVersionsRow>(entity =>
        {
            entity.ToTable("DraftVersions");
            entity.HasKey("DraftId", "VersionKey");
            entity.Property(row => row.DraftId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.AccountId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.VersionKey).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.BlobId).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.Origin).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.Created).HasColumnOrder(5).ValueGeneratedNever();
            entity.HasOne<DraftsRow>().WithMany().HasForeignKey("AccountId", "DraftId").HasPrincipalKey("AccountId", "DraftId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<BlobsRow>().WithMany().HasForeignKey("AccountId", "BlobId").HasPrincipalKey("AccountId", "BlobId").OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<OutboxSubmissionsRow>(entity =>
        {
            entity.ToTable("OutboxSubmissions");
            entity.HasKey("SubmissionId");
            entity.HasAlternateKey("AccountId", "SubmissionId");
            entity.Property(row => row.SubmissionId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.AccountId).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.SourceDraftId).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.BlobId).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.Frozen).HasColumnOrder(4).ValueGeneratedNever();
            entity.Property(row => row.State).HasColumnOrder(5).ValueGeneratedNever();
            entity.Property(row => row.Revision).HasColumnOrder(6).ValueGeneratedNever();
            entity.Property(row => row.Evidence).HasColumnOrder(7).ValueGeneratedNever();
            entity.Property(row => row.Created).HasColumnOrder(8).ValueGeneratedNever();
            entity.Property(row => row.Predecessor).HasColumnOrder(9).ValueGeneratedNever();
            entity.HasOne<DraftsRow>().WithMany().HasForeignKey("AccountId", "SourceDraftId").HasPrincipalKey("AccountId", "DraftId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<BlobsRow>().WithMany().HasForeignKey("AccountId", "BlobId").HasPrincipalKey("AccountId", "BlobId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<OutboxSubmissionsRow>().WithMany().HasForeignKey("AccountId", "Predecessor").HasPrincipalKey("AccountId", "SubmissionId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<AccountsRow>().WithMany().HasForeignKey("AccountId").HasPrincipalKey("AccountId").OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<SubmissionRecipientsRow>(entity =>
        {
            entity.ToTable("SubmissionRecipients");
            entity.HasKey("SubmissionId", "Ordinal");
            entity.Property(row => row.SubmissionId).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.Ordinal).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.Address).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.State).HasColumnOrder(3).ValueGeneratedNever();
            entity.Property(row => row.ReplyCode).HasColumnOrder(4).ValueGeneratedNever();
            entity.HasOne<OutboxSubmissionsRow>().WithMany().HasForeignKey("SubmissionId").HasPrincipalKey("SubmissionId").OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<PreferencesRow>(entity =>
        {
            entity.ToTable("Preferences");
            entity.HasKey("Scope", "Key");
            entity.Property(row => row.Scope).HasColumnOrder(0).ValueGeneratedNever();
            entity.Property(row => row.Key).HasColumnOrder(1).ValueGeneratedNever();
            entity.Property(row => row.Value).HasColumnOrder(2).ValueGeneratedNever();
            entity.Property(row => row.PayloadVersion).HasColumnOrder(3).ValueGeneratedNever();
        });
    }
}
