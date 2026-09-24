# AS24Net.Import

Imports the partners of the old AS2 server (underware.AS2, the `etc` directory of the production server) into
[AS24Net](https://github.com/jskrobak/AS24Net) through its REST API. It is a tool run once (or a few times, while the
old server is still in use), not a part of AS24Net.

## What is imported

The old server keeps one directory per remote AS2 server, `etc/servers/<name>/`:

- `config.json`: the URL, the security (sign, encrypt, required signature / encryption), the MDN, the security
  provider (our certificate and the encryption algorithm), the contacts and the **routings**: the pairs of our AS2 name
  and a partner's AS2 name that use this server. Partners that differ only in their AS2 name are routings of one
  server.
- `cert/<yyyy-MM-dd HHmmss>/<file>`: the partner's certificates, each used from the time (UTC) in the name of its
  directory.

AS24Net has the same split: a **connection** holds the URL, the security, the MDN and the certificates, and a
**partner** is an AS2 name that points to its connection. So:

| Old server | AS24Net |
|---|---|
| server `etc/servers/<name>` | connection `<name>` |
| `ReceiverURL` | *URL* |
| `Sign`, `Encrypt` | *Sign* (SHA-1, as the old server signed; `--signature-algorithm`), *Encrypt* |
| encryption of the security provider (`3DES` by default, `AES-256`) | *Encryption* `3des`, `aes256-cbc` |
| `RequestMDN`, `AsyncMDN`, `RequestSignedMDN` | *MDN* none / synchronous / asynchronous, *Request a signed MDN* |
| `RequireSigned`, `RequireEncrypted` | *Must be signed*, *Must be encrypted* |
| the certificate in use now | *Signature* and *Encryption* certificate |
| a certificate used from a later time | a scheduled certificate change of the connection |
| first contact | *Contact*, *Contact e-mail* (the others go to the description) |
| routing: `PartnerAS2ID`, `PartnerName`, `Enabled` | partner with its connection; enabled when any of its routings is |
| routing: `IdentityAS2ID`, `IdentityName` | identity; the partner's *default identity* when it has only one |
| PKCS#12 of the security provider (`--settings`, `--cert-dir`) | the identity's signing and decryption certificate |

Payloads of the partners are `application/edifact`, as the old server sent them (`--content-type`). `SendDelay` has no
counterpart and is reported.

Everything is created or updated by its name (connection name, AS2 names), so the import can run again: a later run
updates what changed on the old server and does not store a certificate or schedule a certificate change twice.
Settings that are only in AS24Net (e.g. compression, timeouts, HTTP authentication) are left as they are.

## Before the import

1. AS24Net with connections (the `SharedConnections` migration) runs and is reachable.
2. *Settings → API tokens*: create a token with **May change the configuration**. Remove it (or switch the permission
   off by creating a new token) after the import.
3. Copy the old server's `etc` directory (and, for our certificates, its `appsettings.json` and `data/cert`) to the
   machine that runs the import. They hold certificates and passwords; the `.gitignore` of this repository keeps
   `etc/`, `data/` and `*.pfx` out of it.

## Running

```bash
# what would be imported, and the warnings
dotnet run --project src/AS24Net.Import -- --etc ./etc --settings ./appsettings.json --cert-dir ./data/cert --dry-run

# the import
export AS24NET_TOKEN=a24_...
dotnet run --project src/AS24Net.Import -- --etc ./etc --settings ./appsettings.json --cert-dir ./data/cert \
    --url https://as2.example.com
```

| Option | Meaning |
|---|---|
| `--etc <dir>` | `etc` directory of the old server (with `servers/`), or the `servers` directory itself |
| `--url <url>` | address of AS24Net |
| `--token <token>` | API token; better the variable `AS24NET_TOKEN`, which does not end up in the shell history |
| `--settings <file>` | `appsettings.json` of the old server: its security providers name the encryption and our certificate |
| `--cert-dir <dir>` | the directory with our `.pfx` files (the old `data/cert`) |
| `--server <name>` | only this server (repeatable), to move partners one by one |
| `--merge-identical` | servers with the same URL, settings and certificate become one connection |
| `--signature-algorithm <alg>` | digest of our signatures: `sha1` (default), `sha-256`, `sha-384`, `sha-512` |
| `--content-type <type>` | media type of the partners' payloads (default `application/edifact`) |
| `--dry-run` | print the plan and stop |

Without `--settings` the encryption is taken from the name of the security provider (`…AES256` means AES-256,
otherwise 3DES) and the identities get no certificate; set it on the *Identities* page then.

## Warnings

The plan lists what cannot be taken over as it was:

- a partner reached through more than one server: AS24Net has one connection per partner, the first server (by
  name) is taken;
- a partner exchanging messages with more than one of our identities: it gets no default identity, so a message to
  it has to name the identity (`identity` of the REST API, or the choice in the UI);
- an identity signing with different certificates on different servers: the most used one is taken;
- servers with the same URL, settings and certificate: candidates for `--merge-identical`;
- a server without a partner's certificate: encryption and required signatures are switched off for its connection;
- an expired certificate, an encryption AS24Net does not support (RC2: 3DES is used), a name longer than 50
  characters (shortened).

## Tests

```bash
dotnet test
```
