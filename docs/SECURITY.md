# Security and privacy

## STRM and network trust

STRM files must be local regular files, at most 16 KiB, containing exactly one HTTP(S) URL in strict UTF-8. File and ancestor reparse/symlink checks, source-version checks and library scope restrict processing. URLs with user information, fragments or control characters are rejected. These checks do not replace operating-system permissions on the media library.

The initial STRM host is the source authority. Cross-host redirects require administrator trust, and HTTPS-to-HTTP downgrade is rejected. Exact hosts, wildcard suffixes, IPs and CIDRs have distinct meanings: a wildcard excludes the bare suffix domain and does not authorize private IP ranges. Detected hosts are suggestions for administrator review, not automatic approvals.

For direct connections, the transport validates all resolved candidate addresses, connects to validated IPs, and preserves the original Host, TLS SNI and certificate validation. Named services resolving to private, loopback, link-local or special-use addresses need explicit IP/CIDR approval. Literal-IP sources and policy-approved exact-IP redirects constitute explicit address authorization. Trust-rule changes rotate direct connection pools for new requests; already-delivered bodies may finish.

The transport honors .NET's default system/environment proxy and bypass rules. A selected proxy handles destination DNS and egress; destination-IP restrictions then belong to the administrator's proxy configuration. URI, redirect-host and HTTPS checks still apply to plugin-followed redirects. Proxy selection or connection failure terminates the request.

Direct handoff transfers control to the reader. The plugin cannot enforce DNS/IP rules, cancel connections, inspect later responses or schedule requests after that boundary. Relay retains server-side transport checks and capacity limits, but does not prioritize playback over thumbnail generation.

## Tickets and request headers

Playback tickets contain 256 random bits, are stored only in bounded memory, and act as bearer credentials. When an authenticated identity is supplied, it is checked against the user binding. Device digests isolate sessions and leases; they are not authentication. Tickets bind the item, exact media source, STRM version, purpose and runtime generation.

Client tickets start with a ten-minute lifetime; known media duration plus grace can extend this within a 24-hour maximum. Probe and server-job tickets are loopback-only. Probe tickets are limited to their operation budget and revoked on completion. Configuration invalidation and shutdown revoke plugin state and cancel its transport operations; they cannot revoke a URL already handed to a client.

Only supported upstream request headers are forwarded: Range, If-Range, If-None-Match, If-Modified-Since, Accept, Accept-Language, Cache-Control and Pragma. User-Agent is handled as part of the request profile. The internal fast-positioning path may add a validated If-Match guard. Emby authentication, API keys, client cookies and arbitrary headers are not forwarded to the source. The transport has no shared cookie jar.

The gateway is scoped to ticket-authorized resources, not a general URL proxy. Administrator maintenance and diagnostic APIs require Emby's administrator authentication; see [INSTALL.md](INSTALL.md#administrator-apis).

## Persistence

Recovery snapshots store bounded technical fields using schema 3: container, duration, size/bitrate, valid default-stream selections and whitelisted internal-stream properties, including HDR/Dolby Vision and rotation. They exclude URLs, source headers, local paths, titles, credentials and arbitrary codec extradata. Snapshots allow at most 256 internal streams and validate their structure, values and references before use. Older schemas are ignored; invalid primary files use only a validated backup and are otherwise preserved as a cache miss.

Source identities use HMAC rather than persisted plaintext paths or URLs. Extraction state has bounded entries and validated failure/expiry fields. Successful technical writes to Emby remain possible when snapshot saving is disabled. Existing external stream paths remain in Emby's own records, not plugin snapshots.

Configuration necessarily persists selected library IDs and normalized trusted/detected hosts or IP rules. Protect configuration and recovery backups with the same filesystem permissions as other Emby plugin data.

Optional subtitles use authenticated playback sessions bound to the user, video playback session, source version and stream metadata. They consume temporary ASS output from the existing video FFmpeg process, without opening another media URL. Output files have generated names below plugin data storage, fixed task/track/file budgets, no static download route, and no reuse across users or playback sessions. Permissions and source identities are rechecked during reads. Invalidation revokes access immediately; files still being written are deleted after video process exit. Browser windows and temporary files contain subtitle text/styles; local server filesystem access has the same trust boundary as Emby transcoding files. Native font attachment URLs retain host behavior. Resource adaptation requires exact known Web fingerprints and never modifies installed Web files. See [subtitle implementation](SUBTITLE_DESIGN.md).

## Logging and diagnostics

Plugin logs use fixed event/reason codes, mode/status, host ABI, exception type, numeric timing/capacity data and shortened item IDs. They exclude URLs, signatures, local paths, titles, usernames, device names, full User-Agent, authentication headers and FFmpeg command lines. Short item IDs are diagnostic identifiers, not anonymization.

Emby, reverse proxies, players and FFmpeg have independent logging behavior. Playback ticket paths, redirect Location values, signed URLs and conditional headers may appear there even when plugin logs are clean. Redact these before sharing and keep retention bounded. Health/Diagnostics exposes version/build, configuration counts and name-free prefix metadata without invoking third-party patch factories.

## Failure and lifecycle behavior

Before routing is committed, ABI, source mapping or mutation failures retain native behavior and revoke newly allocated state. Insufficient fast-positioning evidence retains the original FFmpeg command. Once a gateway response is in progress, failure may terminate the stream rather than restart native playback.

Transport capacity returns 503 with Retry-After, connection failure 502, connection-stage address rejection 403 and control timeout 504 when headers remain writable. A timeout after body delivery begins aborts the stream. Errors do not become media-body caches or reusable final-address leases. Source backoff is bounded and excludes client/runtime cancellation and trust rejection.

Cancellation remains linked to returned body streams until disposal. Configuration changes prevent stale task commits; shutdown cancels requests and clears tickets, leases and plans. Delayed output cleanup checks the exact registered path, latest job ownership and active jobs before deleting through Emby's filesystem API.

Detailed budgets, cache keys, Range validation, HLS resource limits and retry contracts are maintained in the [design](STRM_BRIDGE_DESIGN.md). Regression and deployment checks are in [TESTING.md](TESTING.md).

字幕时间映射仅读取已绑定视频任务的本地首分片，最多 8 MiB；文件名来自宿主输出，不接受客户端路径。不新增远程读取。解析或等待失败不会回退为独立字幕提取。
