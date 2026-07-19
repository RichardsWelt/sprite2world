# Publish and test Sprite2World

## 1. Create the GitHub repository

Create an empty repository named `Sprite2World` on GitHub. Do not add another README, `.gitignore`, or license because these files are already included here.

## 2. Upload this folder

Run the following commands from inside the `GitHub Upload` folder. Replace `YOUR-GITHUB-NAME` with your GitHub user or organization name.

```bash
git init
git add .
git commit -m "Initial Sprite2World release"
git branch -M main
git remote add origin https://github.com/YOUR-GITHUB-NAME/Sprite2World.git
git push -u origin main
```

The **Actions** tab on GitHub should then run `Docker verification` automatically.

## 3. Test like a new user

Use a separate directory so this test cannot reuse files from the development workspace:

```bash
cd /tmp
git clone https://github.com/YOUR-GITHUB-NAME/Sprite2World.git Sprite2World-clean-test
cd Sprite2World-clean-test
docker compose up -d --build --wait
```

Open [http://localhost:3000](http://localhost:3000). The first-start onboarding should appear and the editor should work without an API key. AI buttons unlock after the user validates a personal key in onboarding or Settings.

If another copy already occupies port 3000, either stop it first or use:

```bash
SPRITE2WORLD_PORT=3100 docker compose up -d --build --wait
```

Then open `http://localhost:3100`.

## 4. Verify and stop

```bash
docker compose ps
docker compose down
```

Both services should show `healthy` before shutdown. `docker compose down` preserves project data. Use `docker compose down -v` only when the test data should be deleted deliberately.
