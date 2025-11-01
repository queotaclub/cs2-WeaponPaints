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

# Function to replace text in file (with flexible whitespace matching)
replace_text() {
    local file_path="$1"
    local search_text="$2"
    local replace_text="$3"
    local use_regex="${4:-false}"
    
    if [ ! -f "$PROJECT_ROOT/$file_path" ]; then
        echo -e "${YELLOW}File not found (skipping): $file_path${NC}"
        return
    fi
    
    echo -e "${YELLOW}Replacing text in: $file_path${NC}"
    
    if [ "$use_regex" = "true" ]; then
        # Use perl for regex replacements (more robust)
        perl -i -pe "s|$search_text|$replace_text|gs" "$PROJECT_ROOT/$file_path" 2>/dev/null || {
            echo -e "${YELLOW}Warning: Regex replacement failed, trying sed...${NC}"
            sed -i "s|$search_text|$replace_text|g" "$PROJECT_ROOT/$file_path"
        }
    else
        # Normal sed replacement
        sed -i "s|$search_text|$replace_text|g" "$PROJECT_ROOT/$file_path"
    fi
}

# Function to comment out code block using pattern matching
comment_code_pattern() {
    local file_path="$1"
    local start_pattern="$2"
    local end_pattern="$3"
    local comment_prefix="${4:-//}"
    
    if [ ! -f "$PROJECT_ROOT/$file_path" ]; then
        echo -e "${YELLOW}File not found (skipping): $file_path${NC}"
        return
    fi
    
    echo -e "${YELLOW}Commenting code pattern in: $file_path${NC}"
    
    # Use perl for more robust multiline pattern matching
    perl -i -pe "
        if (/$start_pattern/ .. /$end_pattern/) {
            \$_ = \"$comment_prefix \$_\" unless /^\\s*$comment_prefix/
        }
    " "$PROJECT_ROOT/$file_path" 2>/dev/null || {
        echo -e "${YELLOW}Warning: Pattern commenting failed${NC}"
    }
}

# Function to wrap code blocks in #if directives (simple sed-based approach)
wrap_with_ifdef() {
    local file_path="$1"
    local start_pattern="$2"
    local end_pattern="${3:-}"
    local define_name="${4:-NO_MENUS}"
    
    if [ ! -f "$PROJECT_ROOT/$file_path" ]; then
        echo -e "${YELLOW}File not found (skipping): $file_path${NC}"
        return
    fi
    
    # Skip if already wrapped
    if grep -q "#if !${define_name}" "$PROJECT_ROOT/$file_path" 2>/dev/null; then
        echo -e "${YELLOW}Already wrapped, skipping: $file_path${NC}"
        return
    fi
    
    echo -e "${YELLOW}Wrapping menu code with #if !${define_name} in: $file_path${NC}"
    
    # Simple approach: insert #if before start pattern line
    # Escape pattern for sed
    local escaped_start=$(echo "$start_pattern" | sed 's/[[\.*^$()+?{|]/\\&/g')
    
    # Insert #if !NO_MENUS before the line matching the pattern
    sed -i "/${escaped_start}/i\\
#if !${define_name}\\
" "$PROJECT_ROOT/$file_path"
    
    # Insert #endif after end pattern (if specified)
    if [ -n "$end_pattern" ] && [ "$end_pattern" != "^}" ]; then
        local escaped_end=$(echo "$end_pattern" | sed 's/[[\.*^$()+?{|]/\\&/g')
        sed -i "/${escaped_end}/a\\
#endif // !${define_name}\\
" "$PROJECT_ROOT/$file_path"
    fi
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
                use_regex=$(jq -r ".platforms.${TARGET_PLATFORM}.replacements[$i].regex // false" "$CONFIG_FILE" 2>/dev/null)
                multiline=$(jq -r ".platforms.${TARGET_PLATFORM}.replacements[$i].multiline // false" "$CONFIG_FILE" 2>/dev/null)
                
                wrap_ifdef=$(jq -r ".platforms.${TARGET_PLATFORM}.replacements[$i].wrap_ifdef // false" "$CONFIG_FILE" 2>/dev/null)
                start_line=$(jq -r ".platforms.${TARGET_PLATFORM}.replacements[$i].start_line" "$CONFIG_FILE" 2>/dev/null)
                end_line=$(jq -r ".platforms.${TARGET_PLATFORM}.replacements[$i].end_line" "$CONFIG_FILE" 2>/dev/null)
                
                if [ -n "$file" ] && [ "$file" != "null" ]; then
                    if [ "$wrap_ifdef" = "true" ] && [ -n "$start_line" ] && [ "$start_line" != "null" ]; then
                        wrap_with_ifdef "$file" "$start_line" "${end_line:-"^}"}" "NO_MENUS"
                    elif [ "$multiline" = "true" ]; then
                        replace_multiline_pattern "$file" "$search" "$replace"
                    elif [ -n "$search" ] && [ "$search" != "null" ]; then
                        replace_text "$file" "$search" "$replace" "$use_regex"
                    fi
                fi
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

