# GitHub upload package

This folder is the sanitized public source package for Sprite2World.

- No `.env.local`, API key, browser preference, local project data, build output, or Git history is included.
- `.env.example` contains placeholders only. The normal setup needs no env file because users can enter their own OpenAI API key during onboarding.
- On a fresh browser origin, the onboarding dialog opens because browser preferences are not part of this package.
- The licensed starter sprites are included as requested and are loaded into the shared demo library at first start.
- `bin`, `obj`, `TestResults`, audit captures, and operating-system metadata are excluded.
- GitHub Actions performs a secret-free Docker build and fresh-start health check on every push to `main` and on pull requests.

Upload the contents of this folder as the repository root, not the surrounding `GitHub Upload` folder itself.
