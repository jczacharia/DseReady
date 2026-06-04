# Dse.Confluence.Seeder

Seeds a local Confluence Server/DC instance with realistic, PNC-flavoured content so the Wolverine
ingestion pipeline can be tested against data shaped like the real source. Everything is created through
Confluence's **REST API** (not the DB), so Lucene, caches and the content store stay consistent — which
matters because the backend crawls via CQL, and CQL reads Lucene.

## Run it (single command)

```bash
cd tools/Dse.Confluence.Seeder
./seed.sh                       # or: dotnet run -c Release
```

Re-runnable: existing pages (matched by space + title) are skipped, so a second run only fills gaps.

### Parameters

Override any setting via CLI (`--Section:Key=value`), env var (`SEEDER_Section__Key`), or `appsettings.json`:

```bash
./seed.sh --Confluence:BaseUrl=http://localhost:8090 \
          --Confluence:AdminUsername=admin --Confluence:AdminPassword=admin \
          --Corpus:SyntheticPagesPerSpace=40 \
          --BackdateViaPostgres=true
```

Ports live entirely in config — point the same binary at any instance.

## What gets created

- **5 spaces**: `CYOPS` (CyberArk Ops), `AGO` (Directory Services), `HAR` (Harmony-Web), `SRE`, `DDP`.
- **~195 pages + blog posts** (default), enough to cross Confluence's 50-result CQL page so the
  ingestion's partitioned crawl is exercised across boundaries.
- **Page hierarchies** (home → section → leaf) so ingested `ancestors[]` chains are populated.
- **Author diversity** — pages are POSTed while authenticated as 8 different users, so `createdBy`
  and `version.by` vary. Multi-version pages exercise `version.number > 1`.
- **Attachments** uploaded and referenced (`ri:attachment` links + `ac:image`).
- **Labels** on most pages (`metadata.labels`).
- **Curated pages** derived from the seed data (CyberArk Links/KB/OCP pipeline, AD/LDAP pattern,
  Harmony vulnerability investigation) plus a **kitchen-sink "Showcase" page per space**.

### Storage-format coverage (the Showcase page hits all of these)

relative `ri:page` links · cross-page + same-page anchor links · `ri:attachment` links · absolute
`<a href>` · root-relative `/spaces/...` · `mailto:` · `ri:user` mentions · `ac:image` (attachment
and `ri:url`) · tables (incl. `colspan`/`rowspan`) · ordered/unordered/nested lists · `ac:task-list` ·
`code`/`noformat`/`html` macros with CDATA · `info`/`note`/`warning`/`tip` panels · `expand` · `toc` ·
`status` · `jira` · `anchor` · `ac:emoticon` · `ac:placeholder` · HTML entities & unicode.

Each maps to a branch in the backend's `ConfluenceHtmlCleaner`.

## Data generator — why this design

The seed data is domain-specific corporate IT/security documentation, not generic prose. A pure faker
produces lorem that looks nothing like it; a pure LLM is non-deterministic and needs keys. So:

- **Bogus** (the de-facto .NET faker, deterministic via `RandomSeed`) drives volume, metadata, dates
  and cross-linking.
- A **curated domain corpus** (lifted from the seed files) gives authentic titles, vocabulary and the
  real complex pages.
- A **deterministic storage-format assembler** (`StorageFormat.cs`) guarantees every construct is
  present regardless of RNG.

Hybrid = realistic + repeatable + offline.

## Author users (one-time setup)

Pages are authored as 8 lower-cased users (password `P@ssw0rd123`). They persist in the (persistent)
Postgres volume, so this is rarely needed. To (re)create them on a fresh DB, run this in the browser
console while logged into Confluence as a WebSudo'd admin (Server REST has no headless user-create):

```js
for (const u of [
  ['cpiasente','Cathy Piasente'],['ssubbiah','Saravanan Subbiah'],['dsmith','Derik Smith'],
  ['tkomlenic','Todd Komlenic'],['eschofield','Eric Schofield'],['rchandler','Raymond Chandler'],
  ['sgriffith','Sam Griffith'],['dkeister','Don Keister']])
  await fetch('/rpc/json-rpc/confluenceservice-v2/addUser', {method:'POST',
    headers:{'Content-Type':'application/json'}, credentials:'same-origin',
    body: JSON.stringify([{name:u[0],fullname:u[1],email:u[0]+'@pnc.example'},'P@ssw0rd123'])});
```

If a user is missing the seeder warns and falls back to admin for those pages.

## Optional: historical dates via Postgres

REST always stamps "now". `--BackdateViaPostgres=true` spreads `creationdate`/`lastmoddate` across
history directly in `confluencedb`. **Restart the Confluence container (or run a content re-index)
afterwards** so the new dates flush from cache and into Lucene before ingesting.
