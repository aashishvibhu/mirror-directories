package me.vbu.mirrordirectories.model;

import lombok.AllArgsConstructor;
import lombok.Getter;
import lombok.NoArgsConstructor;
import lombok.Setter;
import me.vbu.mirrordirectories.ui.views.components.DirectorySelectionPanel;

import java.io.File;

@NoArgsConstructor
@AllArgsConstructor
@Getter @Setter
public class SourceDestinationDirectoryPair {
    private File sourceDirectory;
    private File destinationDirectory;

    public SourceDestinationDirectoryPair(DirectorySelectionPanel directorySelectionPanel){
        sourceDirectory = directorySelectionPanel.getDirectoryPair().getSourceDirectory();
        destinationDirectory = directorySelectionPanel.getDirectoryPair().getDestinationDirectory();
    }

    public boolean validateDirectories() {
        return sourceDirectory != null && sourceDirectory.exists() && sourceDirectory.isDirectory() &&
                destinationDirectory != null && destinationDirectory.exists() && destinationDirectory.isDirectory();
    }
}
