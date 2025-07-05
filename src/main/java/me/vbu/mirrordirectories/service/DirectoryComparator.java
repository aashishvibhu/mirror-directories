package me.vbu.mirrordirectories.service;

import lombok.Getter;
import lombok.Setter;
import me.vbu.mirrordirectories.model.SourceDestinationDirectoryPair;
import me.vbu.mirrordirectories.model.filesystem.DirectoryNode;
import me.vbu.mirrordirectories.model.filesystem.FileNode;
import me.vbu.mirrordirectories.model.filesystem.Node;

import java.io.File;
import java.io.IOException;
import java.nio.file.Path;

/**
 * Service for comparing directories using a hierarchical approach.
 * Creates a tree structure representing the differences between directories.
 */
public class DirectoryComparator {

    private DirectoryNode comparisonResult;

    @Setter @Getter
    private SourceDestinationDirectoryPair directoryPair;

    @Setter
    private FileOperation fileOperation;

    @Getter
    private long processedFileCount = -1;

    @Getter
    private long totalFileCount = 0;

    @Getter
    private String currentlyCopyingFileName = "";

    /**
     * Singleton instance
     */
    private static DirectoryComparator instance;

    /**
     * Private constructor to enforce singleton pattern.
     */
    private DirectoryComparator() {
        // Default to copy operation
        this.fileOperation = new CopyFileOperation();
    }

    /**
     * Gets the singleton instance.
     *
     * @return The singleton instance
     */
    public static synchronized DirectoryComparator getInstance() {
        if (instance == null) {
            instance = new DirectoryComparator();
        }
        return instance;
    }

    /**
     * Recursively compares directories and returns a hierarchy of paths that exist in
     * source but not in destination.
     *
     * @return Root node of the missing items hierarchy
     */
    public DirectoryNode compareDirectories() {
        if (directoryPair == null) {
            throw new IllegalStateException("No Directory Pair has been set. Call setDirectoryPair() first.");
        }

        File sourceDir = directoryPair.getSourceDirectory();
        File destDir = directoryPair.getDestinationDirectory();

        if (!directoryPair.validateDirectories()) {
            throw new IllegalArgumentException("Invalid source or destination directory");
        }

        String dirName = sourceDir.getName();
        comparisonResult = new DirectoryNode(dirName);
        compareDirectoriesInternal(sourceDir, destDir, comparisonResult);
        return comparisonResult;
    }

    /**
     * Internal method to recursively compare directories and build the hierarchy.
     *
     * @param sourceDir Source directory
     * @param destDir Destination directory
     * @param parentNode Parent node to add missing items to
     */
    private void compareDirectoriesInternal(File sourceDir, File destDir, DirectoryNode parentNode) {
        File[] sourceContents = sourceDir.listFiles();

        if (sourceContents != null) {
            for (File sourceItem : sourceContents) {
                File destItem = new File(destDir, sourceItem.getName());

                if (!destItem.exists()) {
                    // Item doesn't exist in destination
                    Node newNode;
                    if (sourceItem.isDirectory()) {
                        newNode = new DirectoryNode(sourceItem.getName());
                        // If it's a directory, recursively add all its contents
                        addAllContents(sourceItem, (DirectoryNode) newNode);
                    } else {
                        newNode = new FileNode(sourceItem.getName());
                        totalFileCount++;
                    }
                    parentNode.addChild(newNode);
                } else if (sourceItem.isDirectory() && destItem.isDirectory()) {
                    // Both are directories, check if there's any difference inside
                    DirectoryNode dirNode = new DirectoryNode(sourceItem.getName());
                    compareDirectoriesInternal(sourceItem, destItem, dirNode);

                    // Only add the directory if it has missing children
                    if (dirNode.hasChildren()) {
                        parentNode.addChild(dirNode);
                    }
                }
            }
        }
    }

    /**
     * Recursively adds all contents of a directory to the node hierarchy.
     *
     * @param dir Directory to add
     * @param parentNode Parent node to add contents to
     */
    private void addAllContents(File dir, DirectoryNode parentNode) {
        File[] contents = dir.listFiles();

        if (contents != null) {
            for (File item : contents) {
                Node node;
                if (item.isDirectory()) {
                    node = new DirectoryNode(item.getName());
                    addAllContents(item, (DirectoryNode) node);
                } else {
                    node = new FileNode(item.getName());
                    totalFileCount++;
                }
                parentNode.addChild(node);
            }
        }
    }

    /**
     * Processes all differences using the current file operation strategy
     *
     * @throws IOException If an I/O error occurs during file operations
     */
    public void processMissingItems() throws IOException {
        if (comparisonResult == null) {
            throw new IllegalStateException("No comparison has been performed yet. Call compareDirectories() first.");
        }

        if (directoryPair == null) {
            throw new IllegalStateException("No input provider has been set. Call setDirectoryPair() first.");
        }

        if (fileOperation == null) {
            throw new IllegalStateException("No file operation has been set. Call setFileOperation() first.");
        }

        processMissingItems(comparisonResult);
    }

    /**
     * Processes all differences from a specific node using the current file operation
     *
     * @param rootNode The root node of the differences hierarchy
     * @throws IOException If an I/O error occurs during file operations
     */
    public void processMissingItems(Node rootNode) throws IOException {
        if (rootNode == null) {
            return;
        }
        processNodeContents(rootNode, directoryPair.getSourceDirectory(),
                                   directoryPair.getDestinationDirectory(), "");
    }

    /**
     * For backward compatibility - copies all differences
     *
     * @throws IOException If an I/O error occurs during file operations
     */
    public void copyMissingItems() throws IOException {
        // Set to copy operation if not already
        FileOperation previousOperation = this.fileOperation;
        this.fileOperation = new CopyFileOperation();
        processMissingItems();
        this.fileOperation = previousOperation;
    }

    /**
     * Recursively processes a node and its children using the selected file operation.
     *
     * @param node The node to process
     * @param sourceRoot Source root directory
     * @param destRoot Destination root directory
     * @param relativePath Current relative path
     * @throws IOException If an I/O error occurs during file operations
     */
    private void processNodeContents(Node node, File sourceRoot, File destRoot, String relativePath) throws IOException {
        // Get the source and destination files
        File sourceFile = new File(sourceRoot, relativePath);
        File destFile = new File(destRoot, relativePath);

        // Create parent directories if needed
        if (!ensureParentDirectoryExists(destFile)) {
            return;
        }

        if (node.isDirectory()) {
            // Create the directory
            if (destFile.exists() || destFile.mkdir()) {
                System.out.println(fileOperation.getOperationName() + " directory created or already exists: " + relativePath);

                // Process all children
                DirectoryNode dirNode = (DirectoryNode) node;
                for (Node child : dirNode.getChildren().values()) {
                    processNodeContents(child, sourceRoot, destRoot,
                                                        relativePath + File.separator + child.getName());
                }
            } else {
                System.err.println("Failed to create directory: " + relativePath);
            }
        } else {
            // Process the file using the selected operation
            try {
                Path sourcePath = sourceFile.toPath();
                Path destPath = destFile.toPath();

                if (fileOperation.executeFileOperation(sourcePath, destPath)) {
                    System.out.println(fileOperation.getOperationName() + " file: " + relativePath);
                    currentlyCopyingFileName = relativePath;
                    processedFileCount++;
                }
            } catch (IOException e) {
                System.err.println("Failed to " + fileOperation.getOperationName().toLowerCase() +
                                   " file: " + relativePath + " (" + e.getMessage() + ")");
                throw e;
            }
        }
    }

    /**
     * Ensures the parent directory for a file exists
     *
     * @param file The file whose parent directory should exist
     * @return True if parent exists or was created, false otherwise
     */
    private boolean ensureParentDirectoryExists(File file) {
        if (file.getParentFile() != null && !file.getParentFile().exists()) {
            if (!file.getParentFile().mkdirs()) {
                System.err.println("Failed to create parent directories for: " + file.getPath());
                return false;
            }
        }
        return true;
    }
}
