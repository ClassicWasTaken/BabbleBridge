# Public repository settings

Keep the repository public and unarchived, with issues and fork-based pull
requests available. Enable private vulnerability reporting so the link in
[SECURITY.md](../SECURITY.md) works. Review incoming changes before merging.

The Windows workflow runs on pushes and pull requests. Retain `contents: read`,
SHA-pinned actions, and `persist-credentials: false`. Build untrusted PR code
on GitHub-hosted runners without signing, release, or repository-write secrets.
Keep release publishing separate from PR builds.

Before a public release, review source history, screenshots, Actions logs, and
artifacts for private data. Publish a fresh build without local source paths
or developer symbols, preserve the distribution notices, and verify that no
external tracking module or local profile is included. Retire obsolete release
assets and CI build artifacts when replacing a release series.
