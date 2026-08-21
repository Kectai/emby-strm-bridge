# Testing

## Automated verification

```sh
./scripts/verify.sh
```

The script uses repository-local locations for all mutable tooling state:

- `.local/dotnet-home`
- `.local/nuget/packages`
- `.local/nuget/http-cache`
- `.local/build`
- `.local/test-work`
- `.local/test-results`

Automated coverage includes strict STRM encoding, ancestor symlink rejection and URL policy; HMAC identities; exact-host and label-bounded explicit subdomain redirect policy; playback-default configuration migration; embedded English, Simplified Chinese, and Traditional Chinese administrator resources with English fallback and complete public keys; enabled/disabled playback-source generation with an opaque client route and absolute loopback server path; direct-loopback versus forwarded-request classification; privacy-safe provider lifecycle logging; counts-only administrator dashboard activity without an external notifier, optional notification targeting, new-host warning rearming, and automatic trust retry; retained exact/wildcard trusted-host presentation state and manual host entry; authorized-user ticket binding, local-server redemption, reconnect lifetime, scope and capacity; 30-second lease boundaries; direct 200/206 probe handling; User-Agent isolation; bounded single-flight behavior; abandoned-flight cancellation; global pending capacity; shared failure backoff; burst limits; cache-clear races; coalesced post-scan reruns; cancellation-time state flush; scalar and media-stream repository persistence, compensating rollback, repository-only external-stream preservation, conflict-free external subtitle reindexing, non-playback stream filtering, fresh-probe behavior when only-missing mode is disabled, and selected-library stored-information clearing; changed-STRM re-extraction; bounded whitelist serialization; main/backup recovery; and orphan cleanup. M5.0 tests cover opaque managed route generation, optional allowlisted container hints, unsafe input rejection, record capacity and expiry, Emby-authenticated server integration credentials on the still-authenticated management routes, consumer User-Agent propagation, direct 200/206 rejection, tamper handling, lifecycle invalidation, and suppression of late leases after clearing. Loopback TCP integration tests verify Range and User-Agent headers without following `Location`, delayed-response failure, `Retry-After`, permanent failures, and unsafe `Location` rejection.

`./scripts/package.sh` additionally tests ZIP integrity, an exact entry allowlist, absent ZIP extra fields, and byte equality between the archived DLL and the verified Release build.

## Manual host matrix

Use synthetic hostnames, accounts, signatures, and titles. Never place production URLs or credentials in test logs or fixtures.

| Case | Expected result |
| --- | --- |
| 302 redirect | One first-hop Range request, then direct consumer access |
| Delayed 302 | Bounded by item/source timeout and cancellation |
| 401/403/404/410 | Stable unavailable response; no success cache |
| 408/429/5xx | 503 with bounded `Retry-After`; shared source backoff |
| Unsafe Location | Ticket revoked; no target request |
| Same source, two User-Agents | Separate successful leases |
| Repeated request within 30 seconds | No repeated source resolution |
| Request at/after 30 seconds | New source resolution; no stale fallback |
| Changed STRM after ticket issue | Ticket rejected |
| Rebuilt library with unchanged STRM | Snapshot restored without remote probe |
| Corrupt main snapshot | Valid backup restored |
| Corrupt main and backup | Refuse overwrite and log fixed failure event |
| Managed prototype 302 | One opaque static route resolves to an approved temporary target |
| Managed prototype 200/206 | Unavailable; original source never appears in `Location` |
| Managed prototype after one hour/clear/restart | Unavailable; no stale lease |
| Managed prototype with external-player relay | Two bounded control redirects, media body fetched only from the final target |

Complete the real Emby host checks in [COMPATIBILITY.md](COMPATIBILITY.md) before calling alternate playback stable.
