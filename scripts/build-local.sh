#!/bin/bash

# Local build script for WeaponPaints
# Usage: ./scripts/build-local.sh [linux|windows]

set -e

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Get target platform from argument or default to linux
TARGET_PLATFORM="${1:-linux}"

if [ "$TARGET_PLATFORM" != "linux" ] && [ "$TARGET_PLATFORM" != "windows" ]; then
    echo -e "${RED}Error: TARGET_PLATFORM must be 'linux' or 'windows'${NC}"
    exit 1
fi

echo -e "${GREEN}Building WeaponPaints for ${TARGET_PLATFORM}...${NC}"
echo ""

# Check if .NET is installed
if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}Error: .NET SDK not found. Please install .NET 8.0 SDK${NC}"
    echo "Visit: https://dotnet.microsoft.com/download"
    exit 1
fi

# Check if jq is installed (for patch config)
if ! command -v jq &> /dev/null; then
    echo -e "${YELLOW}Warning: jq not found. Install with: sudo apt-get install jq${NC}"
    echo -e "${YELLOW}Patch config features will be limited without jq${NC}"
fi

# Step 1: Restore dependencies
echo -e "${GREEN}Step 1: Restoring dependencies...${NC}"
dotnet restore WeaponPaints.csproj

# Step 3: Build
echo ""
echo -e "${GREEN}Step 3: Building project...${NC}"
dotnet build WeaponPaints.csproj -c WeaponPaints -o ./WeaponPaints

echo ""
echo -e "${GREEN}Build completed successfully!${NC}"
echo -e "${GREEN}Output directory: ./WeaponPaints${NC}"

