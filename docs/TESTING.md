# Testing and local evidence

Keep source tests, reusable test tools, feature documentation, and written playtest summaries in Git.

Generated QA sessions under `docs/qa/` and playtest capture directories under `docs/playtests/` stay local and are ignored. These include logs, screenshots, videos, machine inventories, build records, recorded agent connections, and per-run JSON results. Historical documentation may refer to these local evidence locations; they are not shipped with a clone.

JSON required by Unity packages, project settings, authored game content, asset attribution, release configuration, or test tools remains versioned. File format alone does not determine whether a file belongs in Git.

Run `python Tools/CheckRepositoryPrivacy.py` to check tracked text files for likely credentials and private home paths. The scan does not inspect earlier commits or binary content.
