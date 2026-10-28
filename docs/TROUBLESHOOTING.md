# Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| "Setup required" screen | `web/.env` missing | Set `VITE_SUPABASE_URL` / `VITE_SUPABASE_ANON_KEY`, rebuild |
| Login works, but no projects | Not a member | Load demo project, create one (organisation admin), or ask an admin to add your e-mail (you must have signed in once) |
| "You do not have permission…" | Role lacks that action | See [PERMISSIONS.md](PERMISSIONS.md); a project admin can change your role |
| "A DPR already exists for … shift" | One original DPR per date/shift/area | Open the existing one from *Daily DPR* |
| "CONFLICT: … changed by someone else" | Another device saved first | *Daily DPR → On this device* → *Keep my version* or *Use server version* |
| Report stuck at "Waiting to synchronise" | Offline / session expired | Check the status bar; tap *Sync now*; sign in again |
| "Quantity exceeds approved quantity" | Cumulative > activity approved qty | Correct the quantity, or tick *Authorised overrun* with a reason/reference, or have the BOQ/activity quantity revised |
| Cannot approve | Own report and self-approval is off | Another approver must approve, or the project admin enables self-approval |
| Camera / mic button shows "Permission denied" | Browser permission blocked | Chrome → site settings → allow Camera/Microphone; HTTPS is required |
| No GPS fix | Indoors / permission | Go outdoors, or type the location |
| Upload failed: "File type … is not allowed" | MIME outside allow-list | Convert (e.g. HEIC→JPG) or extend the list in `core.ts` |
| Upload failed: Drive service error 401/500 | Function secrets wrong / consent app in *Testing* (token expired after 7 days) | Re-issue the refresh token after publishing the consent screen; check `supabase functions logs drive` |
| Files never upload | DPR not yet saved to server | Uploads wait until the report exists on the server; save the draft online |
| Photos missing in PDF | Offline when generating | Generate the PDF online; photos are fetched through the function |
| Schedule shows "Circular dependency" | Loop in links | Remove one of the listed links |
| Dashboard S-curve empty | No baseline / planned dates | Run the scheduler, *Apply dates*, *Freeze baseline* |
| Old version after deploy | Service worker cache | Accept the *Update* prompt, or reload twice |
| Drafts disappeared | Browser storage cleared | Device storage is not a backup — sync daily; request persistent storage in Settings |
