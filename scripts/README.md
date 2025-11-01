# Build Patching Scripts

This directory contains scripts for patching files before building the project, allowing you to remove or modify files based on the target platform.

## Files

- **`patch-build.sh`** - Main patching script that removes platform-specific files
- **`patch-config.json`** - Configuration file for custom patching rules
- **`sync-upstream.sh`** - Helper script to sync from upstream and trigger cleanup

## Usage

### Manual Usage

Run the script manually before building:

```bash
# For Windows builds (removes Linux-specific files)
TARGET_PLATFORM=windows bash scripts/patch-build.sh

# For Linux builds (removes Windows-specific files)
TARGET_PLATFORM=linux bash scripts/patch-build.sh
```

### Automatic Usage

The script is automatically integrated into the GitHub Actions workflow (`.github/workflows/build.yml`). It runs before each build based on the `TARGET_PLATFORM` environment variable.

To change the target platform, edit the workflow file:

```yaml
env:
  TARGET_PLATFORM: "windows"  # or "linux"
```

## Configuration

Edit `patch-config.json` to customize what files are removed or what text replacements are made:

```json
{
  "platforms": {
    "linux": {
      "remove_files": [
        "Patches/MemoryWindows.cs"
      ],
      "replacements": []
    },
    "windows": {
      "remove_files": [
        "Patches/MemoryLinux.cs"
      ],
      "replacements": []
    }
  }
}
```

### Adding Text Replacements

You can add text replacements for specific platforms:

```json
{
  "platforms": {
    "windows": {
      "replacements": [
        {
          "file": "WeaponPaints.cs",
          "search": "// Hardcoded hotfix",
          "replace": "// Patched: Hardcoded hotfix"
        }
      ]
    }
  }
}
```

## Requirements

- **jq** (optional) - For JSON config parsing. Install with:
  ```bash
  sudo apt-get install jq
  ```
  
  If jq is not installed, the script will still work but won't process the JSON config file. Platform-specific file removal will still work.

## How It Works

1. The script reads the `TARGET_PLATFORM` environment variable (defaults to "linux")
2. For Windows builds: removes `Patches/MemoryLinux.cs`
3. For Linux builds: removes `Patches/MemoryWindows.cs`
4. Optionally processes `patch-config.json` for additional customizations
5. Files are permanently removed from the workspace (use git to restore if needed)

## Restoring Files

If you need to restore files after patching (for local testing):

```bash
git checkout Patches/MemoryLinux.cs
git checkout Patches/MemoryWindows.cs
```

Or restore all files:

```bash
git restore .
```

## Git Hook: Automatic Website Cleanup

A **post-merge git hook** is configured (`.git/hooks/post-merge`) that automatically:
- Detects when you sync/merge from upstream (github/main)
- Deletes the `website/` directory
- Ensures `website/` is in `.gitignore`

### Using the Sync Helper Script

Use the helper script for easy upstream syncing:

```bash
# Sync main branch from upstream
./scripts/sync-upstream.sh main

# Or specify a different branch
./scripts/sync-upstream.sh dev
```

The script will:
1. Fetch from the `github` remote
2. Merge the specified branch
3. Trigger the post-merge hook automatically
4. Clean up the website directory

### Manual Syncing

If you manually sync using git commands:

```bash
git fetch github
git merge github/main
# The post-merge hook will run automatically
```

The hook detects merges from `github/main` and performs the cleanup automatically.

## Notes

- Files removed by the script are permanently deleted from the workspace during the build process
- The script is designed to run in CI/CD pipelines where a fresh checkout happens each time
- For local development, you may want to restore files after testing builds
- The script uses colored output for better visibility (green for success, yellow for warnings)
- The git hook runs automatically after merge operations - no manual action needed

