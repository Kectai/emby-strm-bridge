# Architecture

## Data flow

1. `StrmSourcePolicy` opens a local `.strm` as a bounded regular file, rechecks its metadata, strictly decodes UTF-8, and accepts exactly one absolute HTTP(S) record.
2. `HmacIdentityProvider` derives a path storage key and exact-content fingerprint. Plain paths and URLs are never used as identifiers outside process memory.
3. When only-missing mode is enabled, `ExtractionCoordinator` first attempts a matching snapshot restore. Complete items that need a new playback-source classification, or whose classification is old enough for revalidation when a task next runs, receive only the bounded first-hop header request; their existing technical fields are not probed or overwritten. When only-missing mode is disabled, every selected STRM bypasses snapshots and prior failure backoff and receives a fresh media probe. Probing accepts a direct 200/206 media response or validates one redirect, gives that target to Emby's public `AddMediaInfoWithProbeSafe` API, applies only technical fields, and persists a whitelist snapshot.
4. `StrmMediaSourceProvider` can add an alternate HTTP source only when the current STRM fingerprint was classified as a redirecting source. Its URL contains only a 256-bit random capability ticket. `DirectStreamUrl` remains server-relative for clients, while `Path` and `ProbePath` use the server's public local-API helper with a loopback address so server-side consumers do not interpret the route as a filesystem path.
5. `GatewayService` accepts either an authenticated playback-enabled user who can see the item or a direct loopback request with no forwarding headers from a server-side consumer. Both paths require a live scoped ticket, an included item, and an unchanged STRM file; external redemption additionally binds the ticket to the authorized user.
6. `RedirectResolver` requests only the first source URL with automatic redirects and cookies disabled. It validates the first returned `Location`; cross-host targets require an administrator-configured exact-host entry or an explicit label-bounded `*.example.com` subdomain rule. Later redirects followed by Emby or FFmpeg are part of the trusted target boundary.
7. The gateway returns 302 to an approved redirect target. The playback consumer transfers the media directly; direct-media STRM sources are not offered a bridge candidate.
8. An untrusted cross-host result is classified as awaiting approval rather than a generic extraction failure. A deduplicated counts-only warning is persisted in Emby's administrator dashboard activity log; configured external notification services also receive a local configuration-page link. A newly observed host rearms the warning without repeating it for an unchanged pending set. Saving newly trusted hosts cancels stale work, clears extraction backoff, and queues one coalesced retry; a counts-only completion activity and optional external notification are emitted when no further hosts await approval.

## Hard bounds

- STRM file: 16 KiB.
- Extraction concurrency: default 1, maximum 2.
- Extraction timeout: default 120 seconds, range 30–180.
- The first-hop header request reads that current timeout value for every request; it has no conflicting shorter fixed timeout.
- Shared successful probe results per extraction run: 256; overflow remains correct but is not retained for later items.
- Successful redirect lease: 30 seconds, non-sliding.
- Successful lease cache: 256.
- Pending redirect resolutions: 256 globally.
- Waiters per single flight: 64.
- Actual source requests: 16 globally, 2 per source.
- Active source budget/concurrency states: 512.
- Source rate: burst 12, refill 30 requests/minute.
- Tickets: 1024 runtime default, hard implementation maximum 2048; initial lifetime 2 minutes, then known runtime plus a 2-hour reconnect allowance capped at 24 hours after local-server redemption or first authorized user binding.
- Snapshot: 512 KiB and at most 256 non-external streams.
- Extraction state: 16,384 entries and 16 MiB. A redirect classification becomes eligible for revalidation after 1 hour and is checked on the next scheduled, post-scan, or manual extraction run; this does not create an hourly timer. Saturation fails conservative by checking unknown complete items instead of silently baselining them.
- Maintenance sweep: every 30 seconds.

## Emby integration

Only public SDK contracts are used:

- `BasePluginSimpleUI<PluginConfiguration>`
- `IServerEntryPoint`
- `IScheduledTask`
- `ILibraryPostScanTask`
- automatic discovery of `IMediaSourceProvider` and `ILibraryPostScanTask`
- `IMediaSourceManager.AddMediaInfoWithProbeSafe`
- `ILibraryManager.UpdateItems` with external metadata saving disabled, plus `IItemRepository.SaveMediaStreams` for Emby's separate media-stream repository
- `IActivityManager.Create` for persistent counts-only administrator dashboard status
- `INotificationManager` for counts-only administrator warnings and retry summaries
- ServiceStack-style `IService` routes

Technical media streams are read from Emby's media-stream repository before each update rather than assuming the `BaseItem` instance is hydrated. Newly probed video, audio, and internal subtitle streams replace prior internal streams; embedded cover images, attachments, and other non-playback stream types are excluded. External subtitle and other external streams are retained and reindexed only when needed to avoid repository key collisions, with selected subtitle/audio indexes updated to match. The manually invoked clear task applies the same repository rule, removes only internal technical information from selected libraries, and selectively removes the corresponding plugin snapshots and extraction-state entries.

There is no provider-priority setting in the public contract. Playback bridging is enabled by default and can be disabled independently for extraction-only operation, but actual provider selection remains host-dependent pending the real-host M0 matrix documented in [COMPATIBILITY.md](COMPATIBILITY.md). Privacy-safe provider lifecycle events allow that distinction to be diagnosed without logging a source, target, ticket, path, or client identifier.

Before offering an alternate source, the provider also requires Emby's current static source to match the validated STRM URL and rejects sources that require opening, an open token, or request headers.

## Persistence

The runtime creates this structure below Emby's plugin configuration directory:

```text
Emby.StrmBridge/
├── identity.key
├── mediainfo/
│   ├── <hmac-storage-key>.json
│   └── <hmac-storage-key>.json.bak
└── state/
    ├── extraction-state.json
    └── extraction-state.json.bak
```

Snapshot writes are flushed and atomically replaced. A valid backup is used when the main snapshot is corrupt. If both existing copies are corrupt, the store refuses to overwrite them. Extraction state contains only HMAC storage keys, HMAC source fingerprints, failure counts, and retry timestamps. It uses an in-memory dictionary for constant-time item lookup and batches a durable flush at the end of normal, failed, or cancelled extraction/restore runs. Fixed lock stripes and bounded state tables prevent per-item synchronization objects from growing without limit.
