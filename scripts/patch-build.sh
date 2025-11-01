#!/bin/bash

# Build patching script for WeaponPaints
# This script patches files before building based on target platform

set -e  # Exit on error

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Default values
TARGET_PLATFORM="${TARGET_PLATFORM:-linux}"  # 'linux' or 'windows'
PROJECT_ROOT="${PROJECT_ROOT:-$(pwd)}"
CONFIG_FILE="${CONFIG_FILE:-${PROJECT_ROOT}/scripts/patch-config.json}"

echo -e "${GREEN}Starting build patch process for ${TARGET_PLATFORM}...${NC}"

# Check if config file exists
if [ ! -f "$CONFIG_FILE" ]; then
    echo -e "${YELLOW}Warning: Config file not found at $CONFIG_FILE, using defaults${NC}"
    CONFIG_FILE=""
fi

# Function to remove file if it exists
remove_file() {
    local file_path="$1"
    if [ -f "$PROJECT_ROOT/$file_path" ]; then
        echo -e "${YELLOW}Removing: $file_path${NC}"
        rm -f "$PROJECT_ROOT/$file_path"
    else
        echo -e "${YELLOW}File not found (skipping): $file_path${NC}"
    fi
}

# Function to comment out code block (between markers)
comment_code_block() {
    local file_path="$1"
    local start_marker="$2"
    local end_marker="$3"
    local comment_prefix="${4:-//}"
    
    if [ ! -f "$PROJECT_ROOT/$file_path" ]; then
        echo -e "${YELLOW}File not found (skipping): $file_path${NC}"
        return
    fi
    
    # Check if markers exist
    if ! grep -q "$start_marker" "$PROJECT_ROOT/$file_path"; then
        echo -e "${YELLOW}Start marker not found in $file_path (skipping)${NC}"
        return
    fi
    
    echo -e "${YELLOW}Commenting code block in: $file_path${NC}"
    
    # Use sed to comment out the block
    # This is a simple approach - for more complex cases, you might need awk or perl
    if [ "$comment_prefix" = "//" ]; then
        sed -i "/$start_marker/,/$end_marker/ s/^/$comment_prefix /" "$PROJECT_ROOT/$file_path"
    else
        # For multi-line comments or other comment styles
        sed -i "/$start_marker/,/$end_marker/ s|^|$comment_prefix |" "$PROJECT_ROOT/$file_path"
    fi
}

# Function to replace text in file
replace_text() {
    local file_path="$1"
    local search_text="$2"
    local replace_text="$3"
    
    if [ ! -f "$PROJECT_ROOT/$file_path" ]; then
        echo -e "${YELLOW}File not found (skipping): $file_path${NC}"
        return
    fi
    
    echo -e "${YELLOW}Replacing text in: $file_path${NC}"
    sed -i "s|$search_text|$replace_text|g" "$PROJECT_ROOT/$file_path"
}

# Platform-specific patching
if [ "$TARGET_PLATFORM" = "linux" ]; then
    echo -e "${GREEN}Patching for Linux build...${NC}"
    
    # Remove Windows-specific files
    # remove_file "Patches/MemoryWindows.cs"
    
    # Comment out Windows-specific code in Patch.cs if needed
    # This is handled by runtime checks, but you can add specific patches here
    
elif [ "$TARGET_PLATFORM" = "windows" ]; then
    echo -e "${GREEN}Patching for Windows build...${NC}"
    
    # Remove Linux-specific files
    # remove_file "Patches/MemoryLinux.cs"
    
    # Comment out Linux-specific code if needed
fi

# Process config file if it exists
if [ -f "$CONFIG_FILE" ]; then
    echo -e "${GREEN}Processing config file: $CONFIG_FILE${NC}"
    
    # Parse JSON config (requires jq)
    if command -v jq &> /dev/null; then
        # Files to remove for this platform
        files_to_remove=$(jq -r ".platforms.${TARGET_PLATFORM}.remove_files[]?" "$CONFIG_FILE" 2>/dev/null || echo "")
        if [ -n "$files_to_remove" ]; then
            while IFS= read -r file; do
                [ -n "$file" ] && [ "$file" != "null" ] && remove_file "$file"
            done <<< "$files_to_remove"
        fi
        
        # Text replacements
        replace_count=$(jq ".platforms.${TARGET_PLATFORM}.replacements | length" "$CONFIG_FILE" 2>/dev/null || echo "0")
        if [ "$replace_count" -gt 0 ] && [ "$replace_count" != "null" ]; then
            for i in $(seq 0 $((replace_count - 1))); do
                file=$(jq -r ".platforms.${TARGET_PLATFORM}.replacements[$i].file" "$CONFIG_FILE" 2>/dev/null)
                search=$(jq -r ".platforms.${TARGET_PLATFORM}.replacements[$i].search" "$CONFIG_FILE" 2>/dev/null)
                replace=$(jq -r ".platforms.${TARGET_PLATFORM}.replacements[$i].replace" "$CONFIG_FILE" 2>/dev/null)
                [ -n "$file" ] && [ "$file" != "null" ] && [ -n "$search" ] && [ "$search" != "null" ] && replace_text "$file" "$search" "$replace"
            done
        fi
    else
        echo -e "${YELLOW}Note: jq not found. Platform-specific file removal will still work, but JSON config features are disabled.${NC}"
        echo -e "${YELLOW}Install with: sudo apt-get install jq${NC}"
    fi
fi

# Common cleanup - always apply these
echo -e "${GREEN}Applying common patches...${NC}"

# Example: Comment out specific code blocks that aren't needed
# comment_code_block "WeaponPaints.cs" "// Hardcoded hotfix" "//else"

echo -e "${GREEN}Build patch process completed!${NC}"

