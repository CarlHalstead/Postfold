namespace Postfold.Storage.Stores;

internal static class ProfileSchema
{
    internal const int Version = 1;
    internal const string Sql = """
        CREATE TABLE Accounts(
            AccountId INTEGER PRIMARY KEY CHECK(AccountId > 0), ProviderKind TEXT NOT NULL,
            DisplayName TEXT NOT NULL, Configuration TEXT NOT NULL DEFAULT '{}',
            Capabilities TEXT NOT NULL DEFAULT '{}', CredentialReference TEXT NOT NULL DEFAULT '',
            Active INTEGER NOT NULL DEFAULT 1 CHECK(Active IN (0,1)));
        CREATE TABLE Identities(
            IdentityId INTEGER PRIMARY KEY, AccountId INTEGER NOT NULL REFERENCES Accounts,
            Address TEXT NOT NULL, DisplayName TEXT NOT NULL, Signature TEXT NOT NULL,
            IsPrimary INTEGER NOT NULL CHECK(IsPrimary IN (0,1)), UNIQUE(AccountId,IdentityId));
        CREATE UNIQUE INDEX IdentityPrimary ON Identities(AccountId) WHERE IsPrimary=1;
        CREATE TABLE Collections(
            FolderId INTEGER PRIMARY KEY, AccountId INTEGER NOT NULL REFERENCES Accounts,
            RemoteKey TEXT NOT NULL, ParentId INTEGER, Name TEXT NOT NULL, Role INTEGER NOT NULL,
            Kind TEXT NOT NULL DEFAULT 'Mailbox', Selectable INTEGER NOT NULL, Writable INTEGER NOT NULL,
            UidValidity INTEGER NOT NULL, UNIQUE(AccountId,RemoteKey), UNIQUE(AccountId,FolderId),
            FOREIGN KEY(AccountId,ParentId) REFERENCES Collections(AccountId,FolderId));
        CREATE TABLE Messages(
            MessageId INTEGER PRIMARY KEY, AccountId INTEGER NOT NULL REFERENCES Accounts,
            ProviderKey TEXT NOT NULL, MessageIdHeader TEXT, Subject TEXT NOT NULL,
            Participants TEXT NOT NULL, Date INTEGER NOT NULL, Size INTEGER NOT NULL CHECK(Size>=0),
            IsRead INTEGER NOT NULL, Flagged INTEGER NOT NULL, Availability INTEGER NOT NULL,
            Pending INTEGER NOT NULL DEFAULT 0, NativeThreadId TEXT,
            UNIQUE(AccountId,ProviderKey), UNIQUE(AccountId,MessageId));
        CREATE INDEX MessageDate ON Messages(AccountId,Date DESC,MessageId DESC);
        CREATE TABLE Memberships(
            AccountId INTEGER NOT NULL, MessageId INTEGER NOT NULL, FolderId INTEGER NOT NULL,
            SeenGeneration TEXT NOT NULL, PRIMARY KEY(AccountId,MessageId,FolderId),
            FOREIGN KEY(AccountId,MessageId) REFERENCES Messages(AccountId,MessageId),
            FOREIGN KEY(AccountId,FolderId) REFERENCES Collections(AccountId,FolderId));
        CREATE INDEX FolderMessages ON Memberships(AccountId,FolderId,MessageId);
        CREATE TABLE ImapInstances(
            AccountId INTEGER NOT NULL, FolderId INTEGER NOT NULL, UidValidity INTEGER NOT NULL,
            Uid INTEGER NOT NULL CHECK(Uid>0), MessageId INTEGER NOT NULL, ModSeq TEXT,
            PRIMARY KEY(AccountId,FolderId,UidValidity,Uid),
            FOREIGN KEY(AccountId,MessageId) REFERENCES Messages(AccountId,MessageId),
            FOREIGN KEY(AccountId,FolderId) REFERENCES Collections(AccountId,FolderId));
        CREATE INDEX ImapMessage ON ImapInstances(AccountId,MessageId);
        CREATE TABLE Blobs(
            BlobId TEXT PRIMARY KEY, AccountId INTEGER NOT NULL REFERENCES Accounts,
            ObjectName TEXT NOT NULL UNIQUE, Length INTEGER NOT NULL CHECK(Length>=0),
            Hash TEXT NOT NULL, Protected INTEGER NOT NULL CHECK(Protected IN (0,1)),
            LastAccess INTEGER NOT NULL, UNIQUE(AccountId,BlobId));
        CREATE TABLE Bodies(
            AccountId INTEGER NOT NULL, MessageId INTEGER NOT NULL, BlobId TEXT,
            Format TEXT NOT NULL DEFAULT 'Plain', Text TEXT NOT NULL, LastAccess INTEGER NOT NULL,
            PRIMARY KEY(AccountId,MessageId),
            FOREIGN KEY(AccountId,MessageId) REFERENCES Messages(AccountId,MessageId),
            FOREIGN KEY(AccountId,BlobId) REFERENCES Blobs(AccountId,BlobId));
        CREATE TABLE Attachments(
            AttachmentId INTEGER PRIMARY KEY, AccountId INTEGER NOT NULL, MessageId INTEGER NOT NULL,
            PartKey TEXT NOT NULL, FileName TEXT NOT NULL, MediaType TEXT NOT NULL, Size INTEGER NOT NULL,
            BlobId TEXT, UNIQUE(AccountId,MessageId,PartKey),
            FOREIGN KEY(AccountId,MessageId) REFERENCES Messages(AccountId,MessageId),
            FOREIGN KEY(AccountId,BlobId) REFERENCES Blobs(AccountId,BlobId));
        CREATE TABLE SyncState(
            AccountId INTEGER NOT NULL REFERENCES Accounts, Scope TEXT NOT NULL, Generation TEXT NOT NULL,
            Cursor TEXT, ModSequence TEXT NOT NULL, Complete INTEGER NOT NULL, LastSuccess INTEGER NOT NULL,
            PRIMARY KEY(AccountId,Scope));
        CREATE TABLE PendingOperations(
            OperationId TEXT PRIMARY KEY, AccountId INTEGER NOT NULL, MessageId INTEGER NOT NULL,
            PayloadVersion INTEGER NOT NULL CHECK(PayloadVersion=1), Payload TEXT NOT NULL CHECK(json_valid(Payload)),
            State INTEGER NOT NULL, Revision INTEGER NOT NULL, Attempts INTEGER NOT NULL,
            Due INTEGER NOT NULL, Evidence INTEGER NOT NULL,
            FOREIGN KEY(AccountId,MessageId) REFERENCES Messages(AccountId,MessageId));
        CREATE INDEX OperationDue ON PendingOperations(AccountId,State,Due);
        CREATE TABLE Drafts(
            DraftId TEXT PRIMARY KEY, AccountId INTEGER NOT NULL REFERENCES Accounts,
            IdentityId INTEGER, Revision INTEGER NOT NULL, BaseRevision INTEGER NOT NULL,
            PushedRevision INTEGER NOT NULL, BlobId TEXT NOT NULL, RemoteIdentity TEXT, RemoteVersion TEXT,
            Conflict INTEGER NOT NULL, Held INTEGER NOT NULL, UNIQUE(AccountId,DraftId),
            FOREIGN KEY(AccountId,IdentityId) REFERENCES Identities(AccountId,IdentityId),
            FOREIGN KEY(AccountId,BlobId) REFERENCES Blobs(AccountId,BlobId));
        CREATE TABLE DraftVersions(
            DraftId TEXT NOT NULL, AccountId INTEGER NOT NULL, VersionKey TEXT NOT NULL,
            BlobId TEXT NOT NULL, Origin TEXT NOT NULL, Created INTEGER NOT NULL,
            PRIMARY KEY(DraftId,VersionKey),
            FOREIGN KEY(AccountId,DraftId) REFERENCES Drafts(AccountId,DraftId),
            FOREIGN KEY(AccountId,BlobId) REFERENCES Blobs(AccountId,BlobId));
        CREATE TABLE OutboxSubmissions(
            SubmissionId TEXT PRIMARY KEY, AccountId INTEGER NOT NULL REFERENCES Accounts,
            SourceDraftId TEXT, BlobId TEXT NOT NULL, Frozen TEXT NOT NULL, State INTEGER NOT NULL,
            Revision INTEGER NOT NULL, Evidence TEXT, Created INTEGER NOT NULL, Predecessor TEXT,
            FOREIGN KEY(AccountId,SourceDraftId) REFERENCES Drafts(AccountId,DraftId),
            FOREIGN KEY(AccountId,BlobId) REFERENCES Blobs(AccountId,BlobId),
            UNIQUE(AccountId,SubmissionId),
            FOREIGN KEY(AccountId,Predecessor) REFERENCES OutboxSubmissions(AccountId,SubmissionId));
        CREATE TABLE SubmissionRecipients(
            SubmissionId TEXT NOT NULL REFERENCES OutboxSubmissions, Ordinal INTEGER NOT NULL,
            Address TEXT NOT NULL, State INTEGER NOT NULL, ReplyCode INTEGER,
            PRIMARY KEY(SubmissionId,Ordinal));
        CREATE TABLE Preferences(Scope TEXT NOT NULL, Key TEXT NOT NULL, Value TEXT NOT NULL,
            PayloadVersion INTEGER NOT NULL, PRIMARY KEY(Scope,Key));
        CREATE TABLE SchemaMigrations(Version INTEGER PRIMARY KEY, Applied INTEGER NOT NULL, Hash TEXT NOT NULL);
        CREATE VIRTUAL TABLE SearchText USING fts5(Subject,Participants,Body,tokenize='unicode61 remove_diacritics 2');
        PRAGMA user_version=1;
        """;
}
