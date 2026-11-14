# Releasing

StellarFunctions releases follow the review and verification procedure below.

## Repository and review policy

- Keep the repository public and use `main` as the protected default branch.
- The agent account `killabears724` pushes topic branches and opens pull requests.
  Use draft pull requests while work is in progress.
- Require pull requests and one approving review on `main`. The sole code owner
  is the human repository owner, who must have explicit Write access and provide
  the approval. Do not count the PR author as the approving reviewer.
- Dismiss stale approvals when new commits are pushed, require Code Owner
  review and conversation resolution, and allow squash merging only. The owner
  reviews the evidence manually while CI is deferred.
- Apply the rule to administrators and agents. Block direct pushes, force
  pushes and deletion of `main`. GitHub's branch
  protection and CODEOWNERS review behavior is described in its
  [protected branch documentation](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches)
  and [CODEOWNERS documentation](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners).

The human review account is listed in `.github/CODEOWNERS`. Use a topic
branch and open the PR from `killabears724` for changes the human owner
intends to approve.

## Prepare and publish a release

1. Build and review the candidate from a clean checkout of the proposed changes. Run
   applicable project checks and complete the release validation, including
   exact-build runtime save/load testing before claiming save compatibility.
2. Open a draft PR from `killabears724`, then mark it ready after the change,
   release notes, and validation evidence are reviewable. The human code owner
   reviews and explicitly approves it. If commits change afterward, obtain a
   fresh approval.
3. Squash merge the approved PR. Build from a clean checkout of the resulting
   `main` commit and compare the DLL hash to the tested candidate. Qualify the
   rebuilt DLL if the hash differs.
4. In local release storage, create the numbered `releases/v<major>.<minor>.<patch>/` artifact directory
   with the release DLL and `SHA256SUMS`. Verify the checksum against the exact
   DLL to be published. Keep binaries out of the public Git history.
5. Open a GitHub draft release for the matching version tag, attach the DLL and
   `SHA256SUMS`, and include concise notes describing compatibility and the
   validation actually completed. Verify the tag points to the approved source
   and the attached DLL hash matches `SHA256SUMS`, then publish the release.

Do not describe a release as runtime-validated, save-compatible, or server-ready
unless the corresponding evidence exists. See the project validation record for
the scope and limits of each check.

## Stellar Mod Manager package

Update the root `mod.json` version with the release version and keep its image
paths present in the repository. Include `StellarFunctions.zip` in the draft
release. The archive contains `mod.json`, its referenced images, and
`Mods/StellarFunctions.dll`. Check the archived DLL against the release
checksum and the archived metadata against the tagged source.

After publishing, submit the public repository with the
[Stellar Mod Manager submission form](https://jollyname.dev/submit/).
