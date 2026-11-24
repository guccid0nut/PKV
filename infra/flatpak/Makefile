APP_ID = net.kongor.Willowmaker
MANIFEST = $(APP_ID).yml
BUILD_DIR = build-dir
REPO_DIR = repo

.PHONY: all build build-bundle install run clean

all: build

# Builds the Flatpak application in a clean local environment
build:
	flatpak-builder --force-clean $(BUILD_DIR) $(MANIFEST)

# Builds and installs into current user Flatpak repository
install:
	flatpak-builder --force-clean --user --install $(BUILD_DIR) $(MANIFEST)

# Creates a distributable .flatpak single-file bundle for offline or Steam Deck installation
bundle:
	flatpak-builder --force-clean $(BUILD_DIR) $(MANIFEST)
	flatpak-builder --repo=$(REPO_DIR) --force-clean $(BUILD_DIR) $(MANIFEST)
	flatpak build-bundle $(REPO_DIR) $(APP_ID).flatpak $(APP_ID) stable

# Run the installed Flatpak application
run:
	flatpak run $(APP_ID)

# Clean up ephemeral build artifacts
clean:
	rm -rf $(BUILD_DIR) $(REPO_DIR) .flatpak-builder *.flatpak
