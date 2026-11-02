# Optional OpenID Connect sign-in

Cosine can use one OpenID Connect provider alongside local password login, or
as the only browser sign-in method. It uses Authorization Code flow with PKCE.
The backend handles the protocol and issues the existing Cosine session cookie;
provider tokens are not stored in the browser or database.

OIDC is disabled by default. OAuth-only providers without OIDC discovery and ID
tokens are outside this integration's scope. Administrator permissions remain
managed in Cosine.

## Configure a provider

Register a confidential OIDC client at your provider. Enable Authorization Code
flow and PKCE with S256. Register this exact redirect URI, substituting your
Cosine origin:

```text
https://cosine.example.com/auth/oidc/callback
```

Configure these environment variables on the Cosine container or API process:

```dotenv
Auth__LocalLoginEnabled=true
Auth__Oidc__Enabled=true
Auth__Oidc__DisplayName=Single sign-on
Auth__Oidc__Authority=https://identity.example.com/realms/music
Auth__Oidc__ClientId=cosine
Auth__Oidc__ClientSecret=your-client-secret
Auth__Oidc__PublicOrigin=https://cosine.example.com
Auth__Oidc__AutoProvisionUsers=false
```

`Authority` is the issuer URL, not an authorization endpoint. Cosine obtains
endpoints and signing keys from the issuer's discovery document. The requested
scopes are `openid profile email`. Configure your provider to include `sub` and,
where available, `preferred_username` in ID tokens. Email is imported only when
`email_verified` is true.

`PublicOrigin` is the URL the browser uses, with no path or trailing slash. It
fixes the callback origin independently of proxy host rewriting. HTTPS is
required, except for HTTP loopback origins used in local development.

When using the repository's `compose.yml`, set the corresponding variables in
`.env`; they are already mapped into the API container:

| `.env` variable | Default |
| --- | --- |
| `LOCAL_LOGIN_ENABLED` | `true` |
| `OIDC_ENABLED` | `false` |
| `OIDC_DISPLAY_NAME` | `Single sign-on` |
| `OIDC_AUTHORITY` | Unset |
| `OIDC_CLIENT_ID` | Unset |
| `OIDC_CLIENT_SECRET` | Unset |
| `OIDC_PUBLIC_ORIGIN` | Unset |
| `OIDC_AUTO_PROVISION_USERS` | `false` |
| `OIDC_REQUIRE_HTTPS_METADATA` | `true` |

For a custom Compose file, including the image-only example in README, add the
`Auth__...` entries to the service's `environment` mapping. A host `.env` entry
alone is not passed to a container. Keep client secrets in private deployment
configuration. Restart/recreate the API after changing settings.

## Connect existing accounts

Keep local password login enabled initially:

1. Sign into the existing Cosine account.
2. Within five minutes, open **Profile → Single sign-on → Connect**.
3. Authenticate at the provider to confirm the identity to connect.
4. Sign out of Cosine and verify provider sign-in returns to the same account.

Connecting preserves the local user ID, playlists, history, and admin privileges.
Cosine never merges accounts automatically by email or username. An identity
already connected to another account is rejected. Connecting requires the
original local account session to remain valid through the callback.

With automatic provisioning disabled, an unknown identity is denied access.
With it enabled, a new identity can create a non-admin account without a local
password. Limit who can access the OIDC client at your provider before enabling
this setting. Existing usernames/email addresses cause a conflict requiring
explicit linking. Username display claims are sanitized and bounded; identity
is always keyed by the validated, case-sensitive `(issuer, subject)` pair.

Account names, email, and administrator privileges are not synchronized on
subsequent logins. External-only account passwords are managed at the provider;
Cosine's password-reset interfaces cannot add a local password to them.

## Password-free operation and recovery

After connecting and verifying an administrator account, you may set
`Auth__LocalLoginEnabled=false` (`LOCAL_LOGIN_ENABLED=false` in the supplied
Compose stack). This disables browser password login and legacy Subsonic
password authentication. Subsonic token authentication remains available.

If the provider becomes unavailable, restore local login through deployment
configuration and restart Cosine. Keep a usable local administrator password
for this recovery path. Initial bootstrap still creates a local admin, so
connect that account before switching a new instance to OIDC-only operation.

## Subsonic clients and session lifecycle

OIDC users connect Subsonic clients using the username and separate client token
shown under **Profile → Connect a client**. Their provider password is not a
Subsonic credential.

**Rotate token and sign out all sessions** changes this credential and revokes
existing browser JWTs through the existing security stamp. All paired Subsonic
clients must be reconfigured. Password changes on local accounts have the same
revocation effect. Admin permission changes are reflected when existing browser
sessions next authenticate a request.

Logout clears the current Cosine cookie. It does not log out the identity
provider, revoke other sessions, or rotate Subsonic credentials. Provider account
disablement does not immediately revoke an existing Cosine session or Subsonic
credential. Browser sessions keep the configured `Jwt:ExpirationMinutes`
lifetime (24 hours by default); deleting the local account or rotating its
credentials revokes them locally. Existing SignalR connections need a separate
lifecycle review if immediate disconnection is required.

## Reverse proxies

Preserve the external host and forward scheme/client-address headers. Configure
trusted proxy addresses explicitly, for example:

```dotenv
ReverseProxy__KnownProxies__0=172.20.0.10
```

Do not trust arbitrary forwarded headers. Configure allowed external hosts at
the proxy and through ASP.NET Core's `AllowedHosts`. With an HTTPS OIDC public
origin, Cosine marks its application cookie Secure automatically; explicitly
setting `Auth__SecureCookie=false` in that configuration is rejected.

The exact enabled `/auth/oidc/callback` path is exempt from the application's
same-origin POST gate so protocol callbacks can reach the OIDC handler. State,
correlation, nonce, and token validation still apply. Other `/auth` writes retain
the existing CSRF checks. Configure proxy/access logging to omit query strings
on authentication routes, since callback URLs can contain authorization codes
and protected state.
