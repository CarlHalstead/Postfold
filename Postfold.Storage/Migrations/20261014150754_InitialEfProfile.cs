using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Postfold.Storage.Migrations;

/// <inheritdoc />
public partial class InitialEfProfile : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Accounts",
            columns: table => new
            {
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                ProviderKind = table.Column<string>(type: "TEXT", nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                Configuration = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'{}'"),
                Capabilities = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'{}'"),
                CredentialReference = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "''"),
                Active = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "1")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Accounts", x => x.AccountId);
                table.CheckConstraint("Accounts_AccountId", "AccountId > 0");
                table.CheckConstraint("Accounts_Active", "Active IN (0,1)");
            });

        migrationBuilder.CreateTable(
            name: "Preferences",
            columns: table => new
            {
                Scope = table.Column<string>(type: "TEXT", nullable: false),
                Key = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: false),
                PayloadVersion = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Preferences", x => new { x.Scope, x.Key });
            });

        migrationBuilder.CreateTable(
            name: "Blobs",
            columns: table => new
            {
                BlobId = table.Column<string>(type: "TEXT", nullable: false),
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                ObjectName = table.Column<string>(type: "TEXT", nullable: false),
                Length = table.Column<long>(type: "INTEGER", nullable: false),
                Hash = table.Column<string>(type: "TEXT", nullable: false),
                Protected = table.Column<long>(type: "INTEGER", nullable: false),
                LastAccess = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Blobs", x => x.BlobId);
                table.UniqueConstraint("AK_Blobs_AccountId_BlobId", x => new { x.AccountId, x.BlobId });
                table.UniqueConstraint("AK_Blobs_ObjectName", x => x.ObjectName);
                table.CheckConstraint("Blobs_Length", "Length>=0");
                table.CheckConstraint("Blobs_Protected", "Protected IN (0,1)");
                table.ForeignKey(
                    name: "FK_Blobs_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "AccountId");
            });

        migrationBuilder.CreateTable(
            name: "Collections",
            columns: table => new
            {
                FolderId = table.Column<long>(type: "INTEGER", nullable: false),
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                RemoteKey = table.Column<string>(type: "TEXT", nullable: false),
                ParentId = table.Column<long>(type: "INTEGER", nullable: true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Role = table.Column<long>(type: "INTEGER", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'Mailbox'"),
                Selectable = table.Column<long>(type: "INTEGER", nullable: false),
                Writable = table.Column<long>(type: "INTEGER", nullable: false),
                UidValidity = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Collections", x => x.FolderId);
                table.UniqueConstraint("AK_Collections_AccountId_FolderId", x => new { x.AccountId, x.FolderId });
                table.UniqueConstraint("AK_Collections_AccountId_RemoteKey", x => new { x.AccountId, x.RemoteKey });
                table.ForeignKey(
                    name: "FK_Collections_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "AccountId");
                table.ForeignKey(
                    name: "FK_Collections_Collections_AccountId_ParentId",
                    columns: x => new { x.AccountId, x.ParentId },
                    principalTable: "Collections",
                    principalColumns: new[] { "AccountId", "FolderId" });
            });

        migrationBuilder.CreateTable(
            name: "Identities",
            columns: table => new
            {
                IdentityId = table.Column<long>(type: "INTEGER", nullable: false),
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                Address = table.Column<string>(type: "TEXT", nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                Signature = table.Column<string>(type: "TEXT", nullable: false),
                IsPrimary = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Identities", x => x.IdentityId);
                table.UniqueConstraint("AK_Identities_AccountId_IdentityId", x => new { x.AccountId, x.IdentityId });
                table.CheckConstraint("Identities_IsPrimary", "IsPrimary IN (0,1)");
                table.ForeignKey(
                    name: "FK_Identities_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "AccountId");
            });

        migrationBuilder.CreateTable(
            name: "Messages",
            columns: table => new
            {
                MessageId = table.Column<long>(type: "INTEGER", nullable: false),
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                MessageIdHeader = table.Column<string>(type: "TEXT", nullable: true),
                Subject = table.Column<string>(type: "TEXT", nullable: false),
                Participants = table.Column<string>(type: "TEXT", nullable: false),
                Date = table.Column<long>(type: "INTEGER", nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                IsRead = table.Column<long>(type: "INTEGER", nullable: false),
                Flagged = table.Column<long>(type: "INTEGER", nullable: false),
                Availability = table.Column<long>(type: "INTEGER", nullable: false),
                Pending = table.Column<long>(type: "INTEGER", nullable: false, defaultValueSql: "0"),
                NativeThreadId = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Messages", x => x.MessageId);
                table.UniqueConstraint("AK_Messages_AccountId_MessageId", x => new { x.AccountId, x.MessageId });
                table.UniqueConstraint("AK_Messages_AccountId_ProviderKey", x => new { x.AccountId, x.ProviderKey });
                table.CheckConstraint("Messages_Size", "Size>=0");
                table.ForeignKey(
                    name: "FK_Messages_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "AccountId");
            });

        migrationBuilder.CreateTable(
            name: "SyncState",
            columns: table => new
            {
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                Scope = table.Column<string>(type: "TEXT", nullable: false),
                Generation = table.Column<string>(type: "TEXT", nullable: false),
                Cursor = table.Column<string>(type: "TEXT", nullable: true),
                ModSequence = table.Column<string>(type: "TEXT", nullable: false),
                Complete = table.Column<long>(type: "INTEGER", nullable: false),
                LastSuccess = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SyncState", x => new { x.AccountId, x.Scope });
                table.ForeignKey(
                    name: "FK_SyncState_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "AccountId");
            });

        migrationBuilder.CreateTable(
            name: "Drafts",
            columns: table => new
            {
                DraftId = table.Column<string>(type: "TEXT", nullable: false),
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                IdentityId = table.Column<long>(type: "INTEGER", nullable: true),
                Revision = table.Column<long>(type: "INTEGER", nullable: false),
                BaseRevision = table.Column<long>(type: "INTEGER", nullable: false),
                PushedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                BlobId = table.Column<string>(type: "TEXT", nullable: false),
                RemoteIdentity = table.Column<string>(type: "TEXT", nullable: true),
                RemoteVersion = table.Column<string>(type: "TEXT", nullable: true),
                Conflict = table.Column<long>(type: "INTEGER", nullable: false),
                Held = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Drafts", x => x.DraftId);
                table.UniqueConstraint("AK_Drafts_AccountId_DraftId", x => new { x.AccountId, x.DraftId });
                table.ForeignKey(
                    name: "FK_Drafts_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "AccountId");
                table.ForeignKey(
                    name: "FK_Drafts_Blobs_AccountId_BlobId",
                    columns: x => new { x.AccountId, x.BlobId },
                    principalTable: "Blobs",
                    principalColumns: new[] { "AccountId", "BlobId" });
                table.ForeignKey(
                    name: "FK_Drafts_Identities_AccountId_IdentityId",
                    columns: x => new { x.AccountId, x.IdentityId },
                    principalTable: "Identities",
                    principalColumns: new[] { "AccountId", "IdentityId" });
            });

        migrationBuilder.CreateTable(
            name: "Attachments",
            columns: table => new
            {
                AttachmentId = table.Column<long>(type: "INTEGER", nullable: false),
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                MessageId = table.Column<long>(type: "INTEGER", nullable: false),
                PartKey = table.Column<string>(type: "TEXT", nullable: false),
                FileName = table.Column<string>(type: "TEXT", nullable: false),
                MediaType = table.Column<string>(type: "TEXT", nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                BlobId = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Attachments", x => x.AttachmentId);
                table.UniqueConstraint("AK_Attachments_AccountId_MessageId_PartKey", x => new { x.AccountId, x.MessageId, x.PartKey });
                table.ForeignKey(
                    name: "FK_Attachments_Blobs_AccountId_BlobId",
                    columns: x => new { x.AccountId, x.BlobId },
                    principalTable: "Blobs",
                    principalColumns: new[] { "AccountId", "BlobId" });
                table.ForeignKey(
                    name: "FK_Attachments_Messages_AccountId_MessageId",
                    columns: x => new { x.AccountId, x.MessageId },
                    principalTable: "Messages",
                    principalColumns: new[] { "AccountId", "MessageId" });
            });

        migrationBuilder.CreateTable(
            name: "Bodies",
            columns: table => new
            {
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                MessageId = table.Column<long>(type: "INTEGER", nullable: false),
                BlobId = table.Column<string>(type: "TEXT", nullable: true),
                Format = table.Column<string>(type: "TEXT", nullable: false, defaultValueSql: "'Plain'"),
                Text = table.Column<string>(type: "TEXT", nullable: false),
                LastAccess = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Bodies", x => new { x.AccountId, x.MessageId });
                table.ForeignKey(
                    name: "FK_Bodies_Blobs_AccountId_BlobId",
                    columns: x => new { x.AccountId, x.BlobId },
                    principalTable: "Blobs",
                    principalColumns: new[] { "AccountId", "BlobId" });
                table.ForeignKey(
                    name: "FK_Bodies_Messages_AccountId_MessageId",
                    columns: x => new { x.AccountId, x.MessageId },
                    principalTable: "Messages",
                    principalColumns: new[] { "AccountId", "MessageId" });
            });

        migrationBuilder.CreateTable(
            name: "ImapInstances",
            columns: table => new
            {
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                FolderId = table.Column<long>(type: "INTEGER", nullable: false),
                UidValidity = table.Column<long>(type: "INTEGER", nullable: false),
                Uid = table.Column<long>(type: "INTEGER", nullable: false),
                MessageId = table.Column<long>(type: "INTEGER", nullable: false),
                ModSeq = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ImapInstances", x => new { x.AccountId, x.FolderId, x.UidValidity, x.Uid });
                table.CheckConstraint("ImapInstances_Uid", "Uid>0");
                table.ForeignKey(
                    name: "FK_ImapInstances_Collections_AccountId_FolderId",
                    columns: x => new { x.AccountId, x.FolderId },
                    principalTable: "Collections",
                    principalColumns: new[] { "AccountId", "FolderId" });
                table.ForeignKey(
                    name: "FK_ImapInstances_Messages_AccountId_MessageId",
                    columns: x => new { x.AccountId, x.MessageId },
                    principalTable: "Messages",
                    principalColumns: new[] { "AccountId", "MessageId" });
            });

        migrationBuilder.CreateTable(
            name: "Memberships",
            columns: table => new
            {
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                MessageId = table.Column<long>(type: "INTEGER", nullable: false),
                FolderId = table.Column<long>(type: "INTEGER", nullable: false),
                SeenGeneration = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Memberships", x => new { x.AccountId, x.MessageId, x.FolderId });
                table.ForeignKey(
                    name: "FK_Memberships_Collections_AccountId_FolderId",
                    columns: x => new { x.AccountId, x.FolderId },
                    principalTable: "Collections",
                    principalColumns: new[] { "AccountId", "FolderId" });
                table.ForeignKey(
                    name: "FK_Memberships_Messages_AccountId_MessageId",
                    columns: x => new { x.AccountId, x.MessageId },
                    principalTable: "Messages",
                    principalColumns: new[] { "AccountId", "MessageId" });
            });

        migrationBuilder.CreateTable(
            name: "PendingOperations",
            columns: table => new
            {
                OperationId = table.Column<string>(type: "TEXT", nullable: false),
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                MessageId = table.Column<long>(type: "INTEGER", nullable: false),
                PayloadVersion = table.Column<long>(type: "INTEGER", nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: false),
                State = table.Column<long>(type: "INTEGER", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false),
                Attempts = table.Column<long>(type: "INTEGER", nullable: false),
                Due = table.Column<long>(type: "INTEGER", nullable: false),
                Evidence = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PendingOperations", x => x.OperationId);
                table.CheckConstraint("PendingOperations_Payload", "json_valid(Payload)");
                table.CheckConstraint("PendingOperations_PayloadVersion", "PayloadVersion=1");
                table.ForeignKey(
                    name: "FK_PendingOperations_Messages_AccountId_MessageId",
                    columns: x => new { x.AccountId, x.MessageId },
                    principalTable: "Messages",
                    principalColumns: new[] { "AccountId", "MessageId" });
            });

        migrationBuilder.CreateTable(
            name: "DraftVersions",
            columns: table => new
            {
                DraftId = table.Column<string>(type: "TEXT", nullable: false),
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                VersionKey = table.Column<string>(type: "TEXT", nullable: false),
                BlobId = table.Column<string>(type: "TEXT", nullable: false),
                Origin = table.Column<string>(type: "TEXT", nullable: false),
                Created = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DraftVersions", x => new { x.DraftId, x.VersionKey });
                table.ForeignKey(
                    name: "FK_DraftVersions_Blobs_AccountId_BlobId",
                    columns: x => new { x.AccountId, x.BlobId },
                    principalTable: "Blobs",
                    principalColumns: new[] { "AccountId", "BlobId" });
                table.ForeignKey(
                    name: "FK_DraftVersions_Drafts_AccountId_DraftId",
                    columns: x => new { x.AccountId, x.DraftId },
                    principalTable: "Drafts",
                    principalColumns: new[] { "AccountId", "DraftId" });
            });

        migrationBuilder.CreateTable(
            name: "OutboxSubmissions",
            columns: table => new
            {
                SubmissionId = table.Column<string>(type: "TEXT", nullable: false),
                AccountId = table.Column<long>(type: "INTEGER", nullable: false),
                SourceDraftId = table.Column<string>(type: "TEXT", nullable: true),
                BlobId = table.Column<string>(type: "TEXT", nullable: false),
                Frozen = table.Column<string>(type: "TEXT", nullable: false),
                State = table.Column<long>(type: "INTEGER", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false),
                Evidence = table.Column<string>(type: "TEXT", nullable: true),
                Created = table.Column<long>(type: "INTEGER", nullable: false),
                Predecessor = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxSubmissions", x => x.SubmissionId);
                table.UniqueConstraint("AK_OutboxSubmissions_AccountId_SubmissionId", x => new { x.AccountId, x.SubmissionId });
                table.ForeignKey(
                    name: "FK_OutboxSubmissions_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "AccountId");
                table.ForeignKey(
                    name: "FK_OutboxSubmissions_Blobs_AccountId_BlobId",
                    columns: x => new { x.AccountId, x.BlobId },
                    principalTable: "Blobs",
                    principalColumns: new[] { "AccountId", "BlobId" });
                table.ForeignKey(
                    name: "FK_OutboxSubmissions_Drafts_AccountId_SourceDraftId",
                    columns: x => new { x.AccountId, x.SourceDraftId },
                    principalTable: "Drafts",
                    principalColumns: new[] { "AccountId", "DraftId" });
                table.ForeignKey(
                    name: "FK_OutboxSubmissions_OutboxSubmissions_AccountId_Predecessor",
                    columns: x => new { x.AccountId, x.Predecessor },
                    principalTable: "OutboxSubmissions",
                    principalColumns: new[] { "AccountId", "SubmissionId" });
            });

        migrationBuilder.CreateTable(
            name: "SubmissionRecipients",
            columns: table => new
            {
                SubmissionId = table.Column<string>(type: "TEXT", nullable: false),
                Ordinal = table.Column<long>(type: "INTEGER", nullable: false),
                Address = table.Column<string>(type: "TEXT", nullable: false),
                State = table.Column<long>(type: "INTEGER", nullable: false),
                ReplyCode = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SubmissionRecipients", x => new { x.SubmissionId, x.Ordinal });
                table.ForeignKey(
                    name: "FK_SubmissionRecipients_OutboxSubmissions_SubmissionId",
                    column: x => x.SubmissionId,
                    principalTable: "OutboxSubmissions",
                    principalColumn: "SubmissionId");
            });

        migrationBuilder.CreateIndex(
            name: "IdentityPrimary",
            table: "Identities",
            column: "AccountId",
            unique: true,
            filter: "IsPrimary=1");

        migrationBuilder.CreateIndex(
            name: "ImapMessage",
            table: "ImapInstances",
            columns: new[] { "AccountId", "MessageId" });

        migrationBuilder.CreateIndex(
            name: "FolderMessages",
            table: "Memberships",
            columns: new[] { "AccountId", "FolderId", "MessageId" });

        migrationBuilder.CreateIndex(
            name: "MessageDate",
            table: "Messages",
            columns: new[] { "AccountId", "Date", "MessageId" },
            descending: new[] { false, true, true });

        migrationBuilder.CreateIndex(
            name: "OperationDue",
            table: "PendingOperations",
            columns: new[] { "AccountId", "State", "Due" });
        migrationBuilder.Sql("CREATE VIRTUAL TABLE SearchText USING fts5(Subject,Participants,Body,tokenize='unicode61 remove_diacritics 2');");
        migrationBuilder.Sql("CREATE INDEX UnifiedMessageDate ON Messages(Date DESC,MessageId DESC);");
        migrationBuilder.Sql("PRAGMA user_version=2;");

    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new System.NotSupportedException("Profile downgrades require explicit user-directed recovery; migrations never roll queues backward.");
    }
}
