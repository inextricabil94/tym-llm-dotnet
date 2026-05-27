# Create the GitHub repository

This project is ready to publish as a GitHub repository.

## Option A: GitHub CLI

Install and authenticate GitHub CLI, then run the included script from the repository root.

```bash
gh auth login
./scripts/create-github-repo.sh tym-llm-dotnet private
```

Use `public`, `private`, or `internal` as the second argument.

For an organization repository, include the owner:

```bash
./scripts/create-github-repo.sh your-org/tym-llm-dotnet private
```

The script will:

1. Initialize Git locally if needed.
2. Create the initial commit.
3. Rename the current branch to `main`.
4. Create the GitHub repository.
5. Push the local project to GitHub.

## Option B: Manual Git commands

Create an empty repository on GitHub first, then run:

```bash
git init
git add .
git commit -m "Initial TYM LLM .NET prototype"
git branch -M main
git remote add origin git@github.com:YOUR_USER_OR_ORG/tym-llm-dotnet.git
git push -u origin main
```

## Notes

Do not commit local secrets. Keep `OPENAI_API_KEY` in your shell environment, local user secrets, or GitHub Actions secrets.
