#!/bin/bash

# Helper script to sync from upstream and automatically clean up
# Usage: ./scripts/sync-upstream.sh [branch]
# Example: ./scripts/sync-upstream.sh main

set -e

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

BRANCH="${1:-main}"

echo -e "${BLUE}Syncing from upstream (github/$BRANCH)...${NC}"

# Check if github remote exists
if ! git remote | grep -q "^github$"; then
    echo -e "${YELLOW}Warning: 'github' remote not found. Please add it with:${NC}"
    echo -e "${YELLOW}  git remote add github <upstream-url>${NC}"
    exit 1
fi

# Fetch latest from upstream
echo -e "${GREEN}Fetching from github...${NC}"
git fetch github

# Check if branch exists
if ! git rev-parse --verify "github/$BRANCH" >/dev/null 2>&1; then
    echo -e "${YELLOW}Error: Branch github/$BRANCH not found${NC}"
    exit 1
fi

# Merge from upstream (this will trigger post-merge hook)
echo -e "${GREEN}Merging github/$BRANCH into $BRANCH...${NC}"
git merge "github/$BRANCH" --no-edit || {
    echo -e "${YELLOW}Merge conflicts detected. Please resolve and commit.${NC}"
    exit 1
}

echo -e "${GREEN}Sync completed!${NC}"
echo -e "${YELLOW}Note: The post-merge hook should have cleaned up the website/ directory.${NC}"

