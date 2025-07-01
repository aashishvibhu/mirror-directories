package me.vbu.mirrordirectories.ui.views;

import javafx.application.Platform;
import javafx.geometry.Insets;
import javafx.geometry.Pos;
import javafx.scene.control.*;
import javafx.scene.input.Clipboard;
import javafx.scene.input.ClipboardContent;
import javafx.scene.input.KeyCode;
import javafx.scene.input.KeyCodeCombination;
import javafx.scene.input.KeyCombination;
import javafx.scene.layout.*;
import javafx.stage.DirectoryChooser;
import me.vbu.mirrordirectories.model.DirectoryNode;
import me.vbu.mirrordirectories.model.Node;
import me.vbu.mirrordirectories.service.DirectoryComparator;

import java.io.File;
import java.io.IOException;

/**
 * Main view for the Directory Mirror application.
 */
public class MainView {
    // UI Components
    private final BorderPane root;
    private TextField sourceDirectoryField;
    private TextField destinationDirectoryField;
    private final TreeView<String> directoryDiffTree;
    private Button compareButton;
    private Button copyAllButton;
    private Label statusLabel;

    // Service
    private final DirectoryComparator comparator;

    // Current state
    private File sourceDirectory;
    private File destinationDirectory;
    private DirectoryNode comparisonResult;

    public MainView() {
        // Initialize services
        comparator = DirectoryComparator.getInstance();

        // Create root container
        root = new BorderPane();
        root.setPadding(new Insets(10));

        // Create directory selection section
        GridPane directorySelectionPane = createDirectorySelectionPane();
        root.setTop(directorySelectionPane);

        // Create directory diff tree view
        directoryDiffTree = new TreeView<>();
        directoryDiffTree.setShowRoot(true);
        directoryDiffTree.setRoot(new TreeItem<>("Directory Differences"));

        // Create tree container with scroll pane
        ScrollPane scrollPane = new ScrollPane(directoryDiffTree);
        scrollPane.setFitToWidth(true);
        scrollPane.setFitToHeight(true);
        VBox treeContainer = new VBox(scrollPane);
        treeContainer.setPadding(new Insets(10, 0, 10, 0));
        VBox.setVgrow(scrollPane, Priority.ALWAYS);
        root.setCenter(treeContainer);

        // Create bottom control section
        HBox bottomControls = createBottomControls();
        root.setBottom(bottomControls);

        // Initialize UI state
        copyAllButton.setDisable(true);
        statusLabel.setText("Select directories and click Compare");
    }

    /**
     * Creates the directory selection pane with browse buttons.
     *
     * @return GridPane containing directory selection UI
     */
    private GridPane createDirectorySelectionPane() {
        GridPane grid = new GridPane();
        grid.setHgap(10);
        grid.setVgap(10);
        grid.setPadding(new Insets(0, 0, 10, 0));

        // Labels
        Label sourceLabel = new Label("Source Directory:");
        Label destLabel = new Label("Destination Directory:");

        // Text fields
        sourceDirectoryField = new TextField();
        sourceDirectoryField.setPromptText("Select source directory");
        // Make it read-only but allow copy functionality
        sourceDirectoryField.setEditable(false);
        setupCopyPasteSupport(sourceDirectoryField);

        destinationDirectoryField = new TextField();
        destinationDirectoryField.setPromptText("Select destination directory");
        // Make it read-only but allow copy functionality
        destinationDirectoryField.setEditable(false);
        setupCopyPasteSupport(destinationDirectoryField);

        // Browse buttons
        Button sourceBrowseButton = new Button("Browse...");
        sourceBrowseButton.setOnAction(e -> browseForDirectory(true));

        Button destBrowseButton = new Button("Browse...");
        destBrowseButton.setOnAction(e -> browseForDirectory(false));

        // Compare button
        compareButton = new Button("Compare Directories");
        compareButton.setOnAction(e -> compareDirectories());

        // Layout
        grid.add(sourceLabel, 0, 0);
        grid.add(sourceDirectoryField, 1, 0);
        grid.add(sourceBrowseButton, 2, 0);

        grid.add(destLabel, 0, 1);
        grid.add(destinationDirectoryField, 1, 1);
        grid.add(destBrowseButton, 2, 1);

        grid.add(compareButton, 1, 2);

        // Column constraints
        ColumnConstraints labelColumn = new ColumnConstraints();
        labelColumn.setHgrow(Priority.NEVER);

        ColumnConstraints fieldColumn = new ColumnConstraints();
        fieldColumn.setHgrow(Priority.ALWAYS);
        fieldColumn.setFillWidth(true);

        ColumnConstraints buttonColumn = new ColumnConstraints();
        buttonColumn.setHgrow(Priority.NEVER);

        grid.getColumnConstraints().addAll(labelColumn, fieldColumn, buttonColumn);

        return grid;
    }

    /**
     * Sets up copy-paste functionality for a TextField
     *
     * @param textField The text field to configure
     */
    private void setupCopyPasteSupport(TextField textField) {
        // Enable copy functionality (Ctrl+C)
        textField.setOnKeyPressed(event -> {
            KeyCombination copyComb = new KeyCodeCombination(KeyCode.C, KeyCombination.CONTROL_DOWN);
            if (copyComb.match(event) && textField.getSelectedText() != null && !textField.getSelectedText().isEmpty()) {
                ClipboardContent content = new ClipboardContent();
                content.putString(textField.getSelectedText());
                Clipboard.getSystemClipboard().setContent(content);
                event.consume();
            }
        });

        // Add context menu for copy
        ContextMenu contextMenu = new ContextMenu();
        MenuItem copyMenuItem = new MenuItem("Copy");
        copyMenuItem.setOnAction(event -> {
            ClipboardContent content = new ClipboardContent();
            content.putString(textField.getText());
            Clipboard.getSystemClipboard().setContent(content);
        });
        contextMenu.getItems().add(copyMenuItem);
        textField.setContextMenu(contextMenu);
    }

    /**
     * Creates the bottom control section with Copy All button and status label.
     *
     * @return HBox containing bottom controls
     */
    private HBox createBottomControls() {
        HBox controls = new HBox(10);
        controls.setPadding(new Insets(10, 0, 0, 0));
        controls.setAlignment(Pos.CENTER_LEFT);

        // Copy All button
        copyAllButton = new Button("Copy All Missing Files");
        copyAllButton.setOnAction(e -> copyAllMissingFiles());

        // Status label
        statusLabel = new Label("Ready");
        HBox.setHgrow(statusLabel, Priority.ALWAYS);

        controls.getChildren().addAll(copyAllButton, statusLabel);

        return controls;
    }

    /**
     * Opens a directory chooser dialog and updates the appropriate text field.
     *
     * @param isSource Whether selecting source (true) or destination (false) directory
     */
    private void browseForDirectory(boolean isSource) {
        DirectoryChooser chooser = new DirectoryChooser();
        chooser.setTitle(isSource ? "Select Source Directory" : "Select Destination Directory");

        // Set initial directory if previously selected
        if (isSource && sourceDirectory != null) {
            chooser.setInitialDirectory(sourceDirectory);
        } else if (!isSource && destinationDirectory != null) {
            chooser.setInitialDirectory(destinationDirectory);
        }

        // Show dialog and get selected directory
        File selectedDir = chooser.showDialog(root.getScene().getWindow());

        if (selectedDir != null) {
            if (isSource) {
                sourceDirectory = selectedDir;
                sourceDirectoryField.setText(selectedDir.getAbsolutePath());
            } else {
                destinationDirectory = selectedDir;
                destinationDirectoryField.setText(selectedDir.getAbsolutePath());
            }
        }
    }

    /**
     * Compares the selected directories and updates the tree view.
     */
    private void compareDirectories() {
        // Validate directories
        if (sourceDirectory == null || !sourceDirectory.exists() || !sourceDirectory.isDirectory()) {
            showAlert("Invalid Source Directory", "Please select a valid source directory.");
            return;
        }

        if (destinationDirectory == null || !destinationDirectory.exists() || !destinationDirectory.isDirectory()) {
            showAlert("Invalid Destination Directory", "Please select a valid destination directory.");
            return;
        }

        // Update status
        statusLabel.setText("Comparing directories...");

        // Run comparison in background thread to avoid UI freezing
        new Thread(() -> {
            comparisonResult = comparator.compareDirectories(sourceDirectory, destinationDirectory);

            // Update UI on JavaFX thread
            Platform.runLater(() -> {
                updateTreeView(comparisonResult);

                // Update UI state
                copyAllButton.setDisable(!comparisonResult.hasChildren());

                if (!comparisonResult.hasChildren()) {
                    statusLabel.setText("No differences found. Directories are in sync.");
                } else {
                    int itemCount = countItems(comparisonResult) - 1; // -1 to exclude root
                    statusLabel.setText(itemCount + " items missing from destination directory.");
                }
            });
        }).start();
    }

    /**
     * Updates the tree view with comparison results.
     *
     * @param rootNode The root node of the comparison result
     */
    private void updateTreeView(DirectoryNode rootNode) {
        // Clear existing tree
        TreeItem<String> root = new TreeItem<>(rootNode.getName() + " (Root)");
        root.setExpanded(true);

        // Build tree items from comparison results
        if (rootNode.hasChildren()) {
            for (Node child : rootNode.getChildren().values()) {
                addNodeToTree(child, root);
            }
        }

        directoryDiffTree.setRoot(root);
    }

    /**
     * Recursively adds nodes to the tree view.
     *
     * @param node The node to add
     * @param parent The parent tree item
     */
    private void addNodeToTree(Node node, TreeItem<String> parent) {
        // Create tree item for this node
        String displayName = node.getName() + (node.isDirectory() ? "/" : "");
        TreeItem<String> item = new TreeItem<>(displayName);

        // Add children if this is a directory
        if (node.isDirectory()) {
            item.setExpanded(true);
            DirectoryNode dirNode = (DirectoryNode) node;
            for (Node child : dirNode.getChildren().values()) {
                addNodeToTree(child, item);
            }
        }

        // Add to parent
        parent.getChildren().add(item);
    }

    /**
     * Copies all missing files from source to destination.
     */
    private void copyAllMissingFiles() {
        if (comparisonResult == null || !comparisonResult.hasChildren()) {
            showAlert("Nothing to Copy", "There are no differences to copy.");
            return;
        }

        // Update status
        statusLabel.setText("Copying files...");
        copyAllButton.setDisable(true);

        // Run copy operation in background thread
        new Thread(() -> {
            try {
                int copyCount = comparator.copyMissingItems();

                // Update UI on JavaFX thread
                Platform.runLater(() -> {
                    statusLabel.setText("Successfully copied " + copyCount + " items.");
                    showAlert("Copy Complete", "Successfully copied " + copyCount + " items.");

                    // Refresh comparison
                    compareDirectories();
                });
            } catch (IOException e) {
                // Handle errors
                Platform.runLater(() -> {
                    statusLabel.setText("Error during copy: " + e.getMessage());
                    showAlert("Copy Error", "An error occurred while copying files: " + e.getMessage());
                    copyAllButton.setDisable(false);
                });
            }
        }).start();
    }

    /**
     * Shows an alert dialog with the specified title and message.
     *
     * @param title Alert dialog title
     * @param message Alert dialog message
     */
    private void showAlert(String title, String message) {
        Alert alert = new Alert(Alert.AlertType.INFORMATION);
        alert.setTitle(title);
        alert.setHeaderText(null);
        alert.setContentText(message);
        alert.showAndWait();
    }

    /**
     * Counts the total number of nodes in the hierarchy.
     *
     * @param node The root node to count
     * @return Number of nodes in the hierarchy
     */
    private int countItems(Node node) {
        int count = 1; // Count this node

        if (node.isDirectory()) {
            DirectoryNode dirNode = (DirectoryNode) node;
            if (dirNode.getChildren() != null) {
                for (Node child : dirNode.getChildren().values()) {
                    count += countItems(child);
                }
            }
        }

        return count;
    }

    /**
     * Gets the root pane of this view.
     *
     * @return The root pane
     */
    public Pane getRoot() {
        return root;
    }
}
