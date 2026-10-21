# QuickMail: what the first demo (October 2026) learned

App-specific notes for recording QuickMail. Other apps will need their own equivalents.

## Demo data

- Don't use the repo's visual-test fixtures (`Tools/QuickMail.Fixtures`) as they are: "Test User", a
  200-character subject and a deliberately overlong attachment name sound absurd read aloud. Copy
  the fixture project outside the repo, point its `ProjectReference` at the real
  `QuickMail\QuickMail.csproj` (absolute path), and write a realistic set: two accounts (Work,
  Personal), a three-message conversation with two unread, a flagged invoice with an attachment, a
  meeting invitation, a mailing-list message, a couple of calendar events, contacts, a template.
- Seed dates relative to now (`DateTimeOffset.Now`), not the fixture's fixed clock, or every row
  says January.
- Set `cfg.StartupFolder = "AllInboxes"` to open on the unified inbox.
- Use mail hosts that don't exist (`imap.contoso.example`). QuickMail then runs offline from its
  cache, which is honest to say on air and itself a talking point.
- Store placeholder passwords in the VM so Send queues to the Outbox instead of saying "No password
  stored": `cmdkey /generic:QuickMail:<account guid> /user:<account guid> /pass:demo-placeholder`.

## Build and launch

- The VM pool is ARM64: `dotnet publish QuickMail/QuickMail.csproj -c Release -r win-arm64
  --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=false -o <scratch>`.
  ReadyToRun crashed crossgen when cross-compiling; `build.bat publish-arm64` hid that failure.
- Deploy the profile twice: a pristine copy (`demo-profile-clean`) and a working copy
  (`C:\vmtest\demo-profile`). Before every take, kill QuickMail, `robocopy /MIR` the clean copy over
  the working one, launch with `--profileDir C:\vmtest\demo-profile`, and **wait 50 seconds**: the
  connection attempts announce "Connecting to two accounts… No accounts could be connected…
  Offline. Showing cached messages." up to ~55 s after launch, and land in the middle of a clip
  otherwise. To record startup itself, use an `x` step with `-Launch`.

## Behaviour found while recording (as of 0.8.63 work, 2026-10-20)

Possible bugs, reported to Kelly, not fixed in the demo session:
- All Inboxes and the other All-folder aggregates read "No messages" when opened offline after
  startup (`FetchVirtualFolderAsync` asks only connected accounts; startup reads the cache).
- The compose window has no F6 pane cycle. Tab reaches Subject; Alt+Y jumps to the body.
- Calendar Agenda opens with nothing selected and Down does nothing; W (Week) selects the first event.
- After Set as Archive Folder / Set as Startup Folder, focus jumps to All Inboxes.
- Narrator says nothing when folder-tree type-ahead moves the selection; use arrow keys in demos.
- In Message List Fields, Alt+R is both Row type and Reset to Defaults (goes to Reset).
- K (flag) gives no audible confirmation; sending offline queues silently.

Offline limits to say on air rather than demo: Move to Archive (Ctrl+Shift+M) and any move needs
the server ("Archive may not have completed - refreshing"); only sending waits in the Outbox.

## Keys that worked well on air

F6 cycle and Ctrl+1/2/3/9; Ctrl+Shift+P then "gtf" (Go to Folder); Message List Fields from the
palette ("message list"), including Shift+Tab to Row type for conversation fields ("Unread of
total"); Ctrl+Shift+V views; Alt+Down next unread; Ctrl+Shift+S search with
`from:sam budget is:unread`; Ctrl+N with address autocomplete; Ctrl+F7 + Alt+1 and F7 + Alt+C spell
check; Ctrl+Shift+W watch; Ctrl+Shift+C calendar then W; Shift+F10 on messages and folders; Alt+Enter
properties on a message, a folder and an account.
