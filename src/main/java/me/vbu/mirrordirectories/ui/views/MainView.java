package me.vbu.mirrordirectories.ui.views;

import javafx.application.Platform;
import javafx.geometry.Insets;
import javafx.scene.control.Alert;
import javafx.scene.layout.BorderPane;
import javafx.scene.layout.Pane;
import me.vbu.mirrordirectories.model.SourceDestinationDirectoryPair;
import me.vbu.mirrordirectories.model.filesystem.DirectoryNode;
import me.vbu.mirrordirectories.service.DirectoryComparator;
import me.vbu.mirrordirectories.ui.views.components.ControlPanel;
import me.vbu.mirrordirectories.ui.views.components.DirectorySelectionPanel;
import me.vbu.mirrordirectories.ui.views.components.DirectoryTreeView;

import java.io.IOException;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Main view for the Directory Mirror application.
 * This class acts as a coordinator between UI components and services,
 * following the Composite pattern.
 */
public class MainView {
    // UI Components
    private final BorderPane root;
    private final DirectorySelectionPanel directorySelectionPanel;
    private final DirectoryTreeView directoryTreeView;
    private final ControlPanel controlPanel;

    // Services
    private final DirectoryComparator comparator;

    // Current state
    private DirectoryNode comparisonResult;

    public MainView() {
        // Initialize services
        comparator = DirectoryComparator.getInstance();

        // Create root container
        root = new BorderPane();
        root.setPadding(new Insets(10));

        // Create UI components
        directorySelectionPanel = new DirectorySelectionPanel();
        directoryTreeView = new DirectoryTreeView();
        controlPanel = new ControlPanel();

        // Connect UI components to the layout
        root.setTop(directorySelectionPanel);
        root.setCenter(directoryTreeView);
        root.setBottom(controlPanel);

        // Connect UI components to the services
        connectComponents();
    }

    /**
     * Sets up event handlers and callbacks between components
     */
    private void connectComponents() {
        // Set up directory selection panel compare button
        directorySelectionPanel.setOnCompareCallback(_ -> compareDirectories());

        // Set up control panel copy button
        controlPanel.setOnCopyAllCallback(_ -> copyAllMissingFiles());

        // Initial UI state
        controlPanel.setCopyButtonEnabled(false);
        controlPanel.setStatusMessage("Select directories and click Compare");
    }

    /**
     * Compares the selected directories and updates the tree view.
     */
    private void compareDirectories() {
        // Connect UI input to the comparator

        comparator.setDirectoryPair(new SourceDestinationDirectoryPair(directorySelectionPanel));

        // Validate directories through the input provider
        if (!comparator.getDirectoryPair().validateDirectories()) {
            showAlert("Invalid Directories", "Please select valid source and destination directories.");
            return;
        }

        // Update status
        controlPanel.setStatusMessage("Comparing directories...");

        // Run comparison in background thread to avoid UI freezing
        new Thread(() -> {
            comparisonResult = comparator.compareDirectories();

            // Update UI on JavaFX thread
            Platform.runLater(() -> {
                directoryTreeView.updateTreeView(comparisonResult);

                // Update UI state
                boolean hasDifferences = comparisonResult.hasChildren();
                controlPanel.setCopyButtonEnabled(hasDifferences);

                if (!hasDifferences) {
                    controlPanel.setStatusMessage("No differences found. Directories are in sync.");
                } else {
                    controlPanel.setStatusMessage(comparator.getTotalFileCount() + " files missing from destination directory.");
                }
            });
        }).start();
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
        controlPanel.setStatusMessage("Copying files...");
        controlPanel.setCopyButtonEnabled(false);
        AtomicBoolean isFileOperationCompleted = new AtomicBoolean(false);

        // Run copy operation in background thread
        new Thread(() -> {
            try {
                Thread updateStatusThread = getStatusUpdationThread(isFileOperationCompleted);

                try {
                    comparator.copyMissingItems();
                } finally {
                    isFileOperationCompleted.set(true);
                    try {
                        updateStatusThread.join(); // Wait for status thread to finish
                    } catch (InterruptedException e) {
                        System.err.println("InterruptedException occurred :: " + e.getMessage());
                    }
                }

                // Update UI on JavaFX thread
                Platform.runLater(() -> {
                    isFileOperationCompleted.set(true);
                    controlPanel.setStatusMessage("Successfully copied " + comparator.getProcessedFileCount() + " items.");
                    showAlert("Copy Complete", "Successfully copied " + comparator.getProcessedFileCount() + " items.");

                    // Refresh comparison
                    compareDirectories();
                });
            } catch (IOException e) {
                // Handle errors
                Platform.runLater(() -> {
                    controlPanel.setStatusMessage("Error during copy: " + e.getMessage());
                    showAlert("Copy Error", "An error occurred while copying files: " + e.getMessage());
                    controlPanel.setCopyButtonEnabled(true);
                });
            }
        }).start();
    }

    private Thread getStatusUpdationThread(AtomicBoolean isFileOperationCompleted) {
        Thread updateStatusThread = new Thread(() -> {
            try {
                Thread.sleep(1000);
            } catch (InterruptedException e) {
                System.err.println("Interruption Exception occurred " + e.getMessage());
            }
            while(!isFileOperationCompleted.get()) {
                Platform.runLater(() -> controlPanel.setStatusMessage(
                        "(" + comparator.getProcessedFileCount() + "/" + comparator.getTotalFileCount() + ") " +
                                "Coping file " + comparator.getCurrentlyCopyingFileName()
                ));

                try {
                    Thread.sleep(500);
                } catch (InterruptedException e) {
                    System.err.println("Interruption Exception occurred " + e.getMessage());
                }
            }
        });

        updateStatusThread.start();
        return updateStatusThread;
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
     * Gets the root pane of this view.
     *
     * @return The root pane
     */
    public Pane getRoot() {
        return root;
    }
}
