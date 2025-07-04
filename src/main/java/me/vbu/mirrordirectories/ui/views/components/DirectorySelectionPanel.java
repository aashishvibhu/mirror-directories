package me.vbu.mirrordirectories.ui.views.components;

import javafx.geometry.Insets;
import javafx.scene.control.Button;
import javafx.scene.control.Label;
import javafx.scene.control.TextField;
import javafx.scene.layout.ColumnConstraints;
import javafx.scene.layout.GridPane;
import javafx.scene.layout.Priority;
import javafx.stage.DirectoryChooser;
import lombok.Getter;
import lombok.Setter;
import me.vbu.mirrordirectories.model.SourceDestinationDirectoryPair;

import java.io.File;
import java.util.function.Consumer;

/**
 * Component for handling directory selection UI.
 */
public class DirectorySelectionPanel extends GridPane {

    private TextField sourceDirectoryField;
    private TextField destinationDirectoryField;

    @Getter @Setter
    private SourceDestinationDirectoryPair directoryPair = new SourceDestinationDirectoryPair();

    @Setter
    private Consumer<Void> onCompareCallback;

    public DirectorySelectionPanel() {
        initializeUI();
    }

    private void initializeUI() {
        this.setHgap(10);
        this.setVgap(10);
        this.setPadding(new Insets(0, 0, 10, 0));

        // Labels
        Label sourceLabel = new Label("Source Directory");
        Label destLabel = new Label("Destination Directory");

        // Text fields
        sourceDirectoryField = new TextField();
        sourceDirectoryField.setPromptText("Select source directory");
        sourceDirectoryField.setEditable(false);

        destinationDirectoryField = new TextField();
        destinationDirectoryField.setPromptText("Select destination directory");
        destinationDirectoryField.setEditable(false);

        // Browse buttons
        Button sourceBrowseButton = new Button("Browse");
        sourceBrowseButton.setOnAction(_ -> browseForDirectory(true));

        Button destBrowseButton = new Button("Browse");
        destBrowseButton.setOnAction(_ -> browseForDirectory(false));

        // Compare button
        Button compareButton = new Button("Compare Directories");
        compareButton.setOnAction(_ -> {
            if (directoryPair.validateDirectories() && onCompareCallback != null) {
                onCompareCallback.accept(null);
            }
        });

        // Layout
        this.add(sourceLabel, 0, 0);
        this.add(sourceDirectoryField, 1, 0);
        this.add(sourceBrowseButton, 2, 0);

        this.add(destLabel, 0, 1);
        this.add(destinationDirectoryField, 1, 1);
        this.add(destBrowseButton, 2, 1);

        this.add(compareButton, 1, 2);

        // Column constraints
        ColumnConstraints labelColumn = new ColumnConstraints();
        labelColumn.setHgrow(Priority.NEVER);

        ColumnConstraints fieldColumn = new ColumnConstraints();
        fieldColumn.setHgrow(Priority.ALWAYS);
        fieldColumn.setFillWidth(true);

        ColumnConstraints buttonColumn = new ColumnConstraints();
        buttonColumn.setHgrow(Priority.NEVER);

        this.getColumnConstraints().addAll(labelColumn, fieldColumn, buttonColumn);
    }

    /**
     * Opens a directory chooser dialog and updates the appropriate text field.
     */
    private void browseForDirectory(boolean isSource) {
        DirectoryChooser chooser = getDirectoryChooser(isSource);

        // Show dialog and get selected directory
        File selectedDir = chooser.showDialog(this.getScene().getWindow());

        if (selectedDir != null) {
            if (isSource) {
                directoryPair.setSourceDirectory(selectedDir);
                sourceDirectoryField.setText(selectedDir.getAbsolutePath());
            } else {
                directoryPair.setDestinationDirectory(selectedDir);
                destinationDirectoryField.setText(selectedDir.getAbsolutePath());
            }
        }
    }

    private DirectoryChooser getDirectoryChooser(boolean isSource) {
        DirectoryChooser chooser = new DirectoryChooser();
        chooser.setTitle(isSource ? "Select Source Directory" : "Select Destination Directory");

        // Set initial directory if previously selected
        if (isSource && directoryPair.getSourceDirectory() != null) {
            chooser.setInitialDirectory(directoryPair.getSourceDirectory());
        } else if (!isSource && directoryPair.getDestinationDirectory() != null) {
            chooser.setInitialDirectory(directoryPair.getDestinationDirectory());
        }
        return chooser;
    }
}
